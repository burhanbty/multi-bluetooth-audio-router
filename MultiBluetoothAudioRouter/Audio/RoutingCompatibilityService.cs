using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class RoutingCompatibilityService : IDisposable
{
    private const int FastHoldMilliseconds = 100;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly AudioEndpointTestRunner _runner;
    private readonly AudioDeviceInspector _inspector;
    private readonly Dictionary<string, RoutingCompatibilityReport> _cache = [];

    public RoutingCompatibilityService(
        AudioEndpointTestRunner runner,
        AudioDeviceInspector inspector)
    {
        _runner = runner;
        _inspector = inspector;
    }

    public event Action<string>? LogMessage;

    public void InvalidateCache()
    {
        lock (_cache)
        {
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        _runGate.Dispose();
    }

    public async Task<RoutingCompatibilityReport> CheckAsync(
        MMDevice sourceDevice,
        IReadOnlyList<OutputRouteConfiguration> outputs,
        int deviceRefreshVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);

        if (outputs.Count == 0)
        {
            throw new ArgumentException("At least one output is required.", nameof(outputs));
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A compatibility preflight is already running.");
        }

        var cacheKey = BuildCacheKey(sourceDevice.ID, outputs, deviceRefreshVersion);

        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    PublishLog("Fast compatibility preflight: cached result used.");
                    return cached.AsCached();
                }
            }

            PublishLog("Fast compatibility preflight started.");
            var descriptors = outputs
                .Select(output => _inspector.Inspect(output.OutputDevice))
                .ToArray();
            var conversionModes = await Task.Run(
                    () => CheckConversionChains(sourceDevice, outputs, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (conversionModes.Failure is not null)
            {
                var formatReport = BuildFormatFailureReport(
                    cacheKey,
                    descriptors,
                    outputs,
                    conversionModes);
                CacheIfStable(formatReport);
                return formatReport;
            }

            var individualAttempts = new List<OutputOpenAttemptResult>();
            var individualScenarios = new List<DiagnosticScenarioResult>();
            for (var index = 0; index < outputs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = outputs[index];
                var scenario = await _runner.RunScenarioAsync(
                        $"preflight-alone-{index}",
                        $"{output.Label} alone",
                        [new EndpointTestTarget(output.Label, output.OutputDevice)],
                        FastHoldMilliseconds,
                        detailedLogging: false,
                        log: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                individualScenarios.Add(scenario);
                individualAttempts.Add(ToIndividualAttempt(
                    output,
                    scenario,
                    conversionModes.Modes[output.RouteId]));
            }

            var forwardScenario = outputs.Count == 1
                ? individualScenarios[0]
                : await RunOrderAsync(
                        "preflight-forward",
                        "Forward order",
                        outputs,
                        cancellationToken)
                    .ConfigureAwait(false);
            var reverseOutputs = outputs.Reverse().ToArray();
            var reverseScenario = outputs.Count == 1
                ? forwardScenario
                : await RunOrderAsync(
                        "preflight-reverse",
                        "Reverse order",
                        reverseOutputs,
                        cancellationToken)
                    .ConfigureAwait(false);

            var forward = ToOrderAttempt(
                "Forward order",
                outputs,
                forwardScenario,
                conversionModes.Modes);
            var reverse = ToOrderAttempt(
                "Reverse order",
                reverseOutputs,
                reverseScenario,
                conversionModes.Modes);
            var classification = RoutingCompatibilityClassifier.Classify(
                individualAttempts,
                forward,
                reverse);
            var report = new RoutingCompatibilityReport(
                classification,
                GetSummary(
                    classification,
                    individualAttempts,
                    forward,
                    reverse,
                    descriptors),
                IsProbabilistic(classification),
                false,
                descriptors,
                individualAttempts,
                forward,
                reverse,
                cacheKey);

            CacheIfStable(report);
            PublishLog($"Fast compatibility preflight result: {classification}.");
            return report;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<DiagnosticScenarioResult> RunOrderAsync(
        string id,
        string label,
        IReadOnlyList<OutputRouteConfiguration> outputs,
        CancellationToken cancellationToken)
    {
        return await _runner.RunScenarioAsync(
                id,
                label,
                outputs.Select(output =>
                        new EndpointTestTarget(output.Label, output.OutputDevice))
                    .ToArray(),
                FastHoldMilliseconds,
                detailedLogging: false,
                log: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ConversionCheckResult CheckConversionChains(
        MMDevice sourceDevice,
        IReadOnlyList<OutputRouteConfiguration> outputs,
        CancellationToken cancellationToken)
    {
        using var capture = new WasapiLoopbackCapture(sourceDevice);
        var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WaveFormat mixFormat;

            try
            {
                mixFormat = AudioFormatAdapter.GetDeviceMixFormat(
                    output.OutputDevice);
            }
            catch (Exception exception)
            {
                return new ConversionCheckResult(
                    modes,
                    output,
                    AudioErrorClassifier.Classify(exception),
                    false);
            }

            try
            {
                var source = new BufferedWaveProvider(capture.WaveFormat)
                {
                    ReadFully = true,
                    DiscardOnBufferOverflow = true
                };
                using var chain = OutputProviderChain.Create(source, mixFormat);
                modes[output.RouteId] = chain.ConversionMode;
            }
            catch (Exception exception)
            {
                return new ConversionCheckResult(
                    modes,
                    output,
                    AudioErrorClassifier.Classify(exception),
                    exception is AudioFormatConversionException);
            }
        }

        return new ConversionCheckResult(modes, null, null, false);
    }

    private static RoutingCompatibilityReport BuildFormatFailureReport(
        string cacheKey,
        IReadOnlyList<AudioDeviceDescriptor> descriptors,
        IReadOnlyList<OutputRouteConfiguration> outputs,
        ConversionCheckResult conversion)
    {
        var failed = conversion.FailedOutput!;
        var error = conversion.Failure!;
        var attempts = outputs.Select((output, index) =>
            new OutputOpenAttemptResult(
                output.OutputDevice.ID,
                output.OutputDevice.FriendlyName,
                output.Label,
                index,
                output.RouteId != failed.RouteId,
                output.RouteId == failed.RouteId
                    ? DiagnosticOperation.Initialize
                    : null,
                output.RouteId == failed.RouteId
                    ? error.FailureCategory
                    : null,
                output.RouteId == failed.RouteId ? error : null,
                conversion.Modes.GetValueOrDefault(output.RouteId, "Unavailable"),
                output.RouteId == failed.RouteId &&
                conversion.IsFormatConversionFailure)).ToArray();
        var emptyOrder = new OutputOrderAttemptResult(
            "Not run",
            outputs.Select(output => output.OutputDevice.ID).ToArray(),
            false,
            attempts.First(attempt =>
                    string.Equals(
                        attempt.DeviceId,
                        failed.OutputDevice.ID,
                        StringComparison.OrdinalIgnoreCase))
                .OpeningOrderIndex,
            failed.OutputDevice.ID,
            DiagnosticOperation.Initialize,
            error.FailureCategory,
            error,
            attempts);

        var classification = conversion.IsFormatConversionFailure
            ? CompatibilityClassification.FormatConversionFailure
            : CompatibilityClassification.UnknownFailure;
        var setupKind = conversion.IsFormatConversionFailure
            ? "Format conversion setup"
            : "Mix-format inspection";

        return new RoutingCompatibilityReport(
            classification,
            $"{setupKind} failed for {failed.Label}: " +
            $"{error.KnownSymbolicName} {error.HResultHex} - " +
            error.UserFacingDescription,
            classification == CompatibilityClassification.UnknownFailure,
            false,
            descriptors,
            attempts,
            emptyOrder,
            emptyOrder with { Name = "Not run (reverse)" },
            cacheKey);
    }

    private static OutputOpenAttemptResult ToIndividualAttempt(
        OutputRouteConfiguration output,
        DiagnosticScenarioResult scenario,
        string conversionMode)
    {
        var failure = scenario.Failure;
        return new OutputOpenAttemptResult(
            output.OutputDevice.ID,
            output.OutputDevice.FriendlyName,
            output.Label,
            0,
            scenario.Succeeded,
            failure?.Operation,
            failure?.ErrorInfo?.FailureCategory,
            failure?.ErrorInfo,
            conversionMode);
    }

    private static OutputOrderAttemptResult ToOrderAttempt(
        string name,
        IReadOnlyList<OutputRouteConfiguration> orderedOutputs,
        DiagnosticScenarioResult scenario,
        IReadOnlyDictionary<string, string> conversionModes)
    {
        var failure = scenario.Failure;
        var failedIndex = failure is null
            ? (int?)null
            : orderedOutputs
                .Select((output, index) => (output, index))
                .Where(item => item.output.OutputDevice.ID == failure.DeviceId)
                .Select(item => (int?)item.index)
                .FirstOrDefault();
        var attempts = orderedOutputs.Select((output, index) =>
            new OutputOpenAttemptResult(
                output.OutputDevice.ID,
                output.OutputDevice.FriendlyName,
                output.Label,
                index,
                scenario.Succeeded || failedIndex is null || index < failedIndex,
                index == failedIndex ? failure?.Operation : null,
                index == failedIndex ? failure?.ErrorInfo?.FailureCategory : null,
                index == failedIndex ? failure?.ErrorInfo : null,
                conversionModes.GetValueOrDefault(output.RouteId, "Unavailable")))
            .ToArray();

        return new OutputOrderAttemptResult(
            name,
            orderedOutputs.Select(output => output.OutputDevice.ID).ToArray(),
            scenario.Succeeded,
            failedIndex,
            failure?.DeviceId ?? string.Empty,
            failure?.Operation,
            failure?.ErrorInfo?.FailureCategory,
            failure?.ErrorInfo,
            attempts);
    }

    private static string GetSummary(
        CompatibilityClassification classification,
        IReadOnlyList<OutputOpenAttemptResult> individuals,
        OutputOrderAttemptResult forward,
        OutputOrderAttemptResult reverse,
        IReadOnlyList<AudioDeviceDescriptor> descriptors)
    {
        var includesBluetooth = descriptors.Any(descriptor =>
            descriptor.TransportKind is
                AudioDeviceTransportKind.BluetoothClassicOrUnknownBluetooth or
                AudioDeviceTransportKind.BluetoothLeAudioCandidate);
        var sharedStackDescription = includesBluetooth
            ? "shared Bluetooth/audio driver or hardware resource"
            : "shared audio driver or hardware resource";

        return classification switch
        {
            CompatibilityClassification.Compatible =>
                $"Passed. {forward.OrderedDeviceIds.Count} output(s) opened " +
                "successfully in forward and reverse order.",
            CompatibilityClassification.IndividualDeviceFailure =>
                $"{individuals.First(attempt => !attempt.Succeeded).Label} " +
                "failed even when tested alone.",
            CompatibilityClassification.SecondEndpointResourceLimitLikely =>
                "Each output works individually, but Windows fails while " +
                "creating the same later opening position in both tested " +
                $"orders. This strongly suggests a {sharedStackDescription} " +
                "limit; it is not a definitive " +
                "hardware diagnosis.",
            CompatibilityClassification.DeviceSpecificSimultaneousFailure =>
                "The same physical output fails during both simultaneous " +
                "opening orders.",
            CompatibilityClassification.OrderSensitive =>
                "One endpoint opening order succeeds while the other fails; " +
                "the driver or endpoint appears order-sensitive.",
            CompatibilityClassification.FormatConversionFailure =>
                "A provider format-conversion chain could not be created.",
            _ => "The compatibility result does not match a known pattern."
        };
    }

    private static bool IsProbabilistic(CompatibilityClassification classification) =>
        classification is
            CompatibilityClassification.SecondEndpointResourceLimitLikely or
            CompatibilityClassification.DeviceSpecificSimultaneousFailure or
            CompatibilityClassification.OrderSensitive or
            CompatibilityClassification.UnknownFailure;

    private void CacheIfStable(RoutingCompatibilityReport report)
    {
        if (report.Classification == CompatibilityClassification.UnknownFailure)
        {
            return;
        }

        lock (_cache)
        {
            _cache[report.CacheKey] = report;
        }
    }

    private static string BuildCacheKey(
        string sourceId,
        IReadOnlyList<OutputRouteConfiguration> outputs,
        int refreshVersion) =>
        $"{refreshVersion}|{sourceId}|" +
        string.Join(">", outputs.Select(output => output.OutputDevice.ID));

    private void PublishLog(string message)
    {
        foreach (Action<string> handler in LogMessage?.GetInvocationList() ?? [])
        {
            try
            {
                handler(message);
            }
            catch
            {
                // UI/log subscribers must not abort preflight cleanup.
            }
        }
    }

    private sealed record ConversionCheckResult(
        IReadOnlyDictionary<string, string> Modes,
        OutputRouteConfiguration? FailedOutput,
        AudioErrorInfo? Failure,
        bool IsFormatConversionFailure);
}
