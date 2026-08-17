using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class HardwareDiagnosticService : IDisposable
{
    private const int FullDiagnosticHoldMilliseconds = 650;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly AudioEndpointTestRunner _runner;
    private readonly AudioDeviceInspector _inspector;

    public HardwareDiagnosticService(
        AudioEndpointTestRunner runner,
        AudioDeviceInspector inspector)
    {
        _runner = runner;
        _inspector = inspector;
    }

    public event Action<string>? LogMessage;

    public void Dispose()
    {
        _runGate.Dispose();
    }

    public async Task<HardwareDiagnosticReport> RunAsync(
        MMDevice? sourceDevice,
        MMDevice outputDevice1,
        MMDevice outputDevice2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputDevice1);
        ArgumentNullException.ThrowIfNull(outputDevice2);

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A hardware diagnostic test is already running.");
        }

        try
        {
            PublishLog("=== Hardware Diagnostic Test started ===");
            var sourceDescriptor = sourceDevice is null
                ? null
                : _inspector.Inspect(sourceDevice);
            var descriptor1 = _inspector.Inspect(outputDevice1);
            var descriptor2 = _inspector.Inspect(outputDevice2);
            LogDescriptor("Output Device 1", descriptor1);
            LogDescriptor("Output Device 2", descriptor2);
            PublishLog(
                "Full diagnostic uses each endpoint's own mix format and silent " +
                "audio. Endpoint creation results are separate from routing " +
                "format-conversion results.");

            var output1Alone = await RunScenarioAsync(
                "output-1-alone",
                "Output 1 alone",
                [new EndpointTestTarget("Output Device 1", outputDevice1)],
                cancellationToken).ConfigureAwait(false);
            var output2Alone = await RunScenarioAsync(
                "output-2-alone",
                "Output 2 alone",
                [new EndpointTestTarget("Output Device 2", outputDevice2)],
                cancellationToken).ConfigureAwait(false);
            var order1Then2 = await RunScenarioAsync(
                "order-1-2",
                "Order 1 -> 2",
                [
                    new EndpointTestTarget("Output Device 1", outputDevice1),
                    new EndpointTestTarget("Output Device 2", outputDevice2)
                ],
                cancellationToken).ConfigureAwait(false);
            var order2Then1 = await RunScenarioAsync(
                "order-2-1",
                "Order 2 -> 1",
                [
                    new EndpointTestTarget("Output Device 2", outputDevice2),
                    new EndpointTestTarget("Output Device 1", outputDevice1)
                ],
                cancellationToken).ConfigureAwait(false);

            var individualAttempts = new[]
            {
                ToIndividualAttempt(
                    "Output Device 1",
                    outputDevice1,
                    output1Alone),
                ToIndividualAttempt(
                    "Output Device 2",
                    outputDevice2,
                    output2Alone)
            };
            var forward = ToOrderAttempt(
                "Order 1 -> 2",
                [outputDevice1, outputDevice2],
                ["Output Device 1", "Output Device 2"],
                order1Then2);
            var reverse = ToOrderAttempt(
                "Order 2 -> 1",
                [outputDevice2, outputDevice1],
                ["Output Device 2", "Output Device 1"],
                order2Then1);
            var classification = RoutingCompatibilityClassifier.Classify(
                individualAttempts,
                forward,
                reverse);
            var summary = GetClassificationSummary(classification);
            var conversionModes = GetConversionModes(
                sourceDevice,
                outputDevice1,
                outputDevice2);
            var technicalReport = BuildTechnicalReport(
                sourceDescriptor,
                [descriptor1, descriptor2],
                conversionModes,
                [output1Alone, output2Alone, order1Then2, order2Then1],
                classification,
                summary);
            var report = new HardwareDiagnosticReport(
                output1Alone,
                output2Alone,
                order1Then2,
                order2Then1,
                summary,
                classification,
                IsProbabilistic(classification),
                [descriptor1, descriptor2],
                technicalReport);

            LogSummary(report);
            PublishLog("Copyable diagnostic environment report is ready.");
            PublishLog("=== Hardware Diagnostic Test finished ===");
            return report;
        }
        catch (OperationCanceledException)
        {
            PublishLog("Hardware Diagnostic Test was cancelled.");
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private Task<DiagnosticScenarioResult> RunScenarioAsync(
        string id,
        string label,
        IReadOnlyList<EndpointTestTarget> targets,
        CancellationToken cancellationToken) =>
        _runner.RunScenarioAsync(
            id,
            label,
            targets,
            FullDiagnosticHoldMilliseconds,
            detailedLogging: true,
            PublishLog,
            cancellationToken);

    private void LogDescriptor(string label, AudioDeviceDescriptor descriptor)
    {
        var qualifier = descriptor.IsTransportClassificationHeuristic
            ? "inferred from endpoint metadata/name"
            : "from endpoint metadata";
        PublishLog($"{label}: {descriptor.FriendlyName} ({descriptor.DeviceId})");
        PublishLog($"{label} state: {descriptor.DeviceState}");
        PublishLog($"{label} transport: {descriptor.TransportKind}, {qualifier}.");
        PublishLog($"{label} transport evidence: {descriptor.TransportEvidence}");
        PublishLog($"{label} mix format: {descriptor.MixFormat}");
    }

    private void LogSummary(HardwareDiagnosticReport report)
    {
        PublishLog("=== Diagnostic Summary ===");
        foreach (var scenario in report.Scenarios)
        {
            var failure = scenario.Failure;
            PublishLog(scenario.Succeeded
                ? $"{scenario.DisplayName}: SUCCESS"
                : $"{scenario.DisplayName}: FAILED at " +
                  $"{failure?.Operation} for {failure?.DeviceLabel} - " +
                  $"{failure?.ErrorInfo?.KnownSymbolicName} " +
                  failure?.ErrorInfo?.HResultHex);
        }

        PublishLog($"Classification: {report.Classification}");
        PublishLog($"Likely classification: {report.LikelyClassification}");

        if (report.Classification ==
            CompatibilityClassification.SecondEndpointResourceLimitLikely)
        {
            var includesBluetooth = report.OutputDevices.Any(device =>
                device.TransportKind is
                    AudioDeviceTransportKind.BluetoothClassicOrUnknownBluetooth or
                    AudioDeviceTransportKind.BluetoothLeAudioCandidate);
            PublishLog(includesBluetooth
                ? "This pattern strongly suggests a shared Bluetooth/audio " +
                  "driver or hardware resource limit, but it is not a definitive " +
                  "hardware diagnosis."
                : "This pattern strongly suggests a shared audio driver or " +
                  "hardware resource limit. The selected outputs were not " +
                  "classified as Bluetooth, and the result is not definitive.");
        }
    }

    private static OutputOpenAttemptResult ToIndividualAttempt(
        string label,
        MMDevice device,
        DiagnosticScenarioResult scenario)
    {
        var failure = scenario.Failure;
        return new OutputOpenAttemptResult(
            device.ID,
            device.FriendlyName,
            label,
            0,
            scenario.Succeeded,
            failure?.Operation,
            failure?.ErrorInfo?.FailureCategory,
            failure?.ErrorInfo,
            "Direct (device mix format)");
    }

    private static OutputOrderAttemptResult ToOrderAttempt(
        string name,
        IReadOnlyList<MMDevice> devices,
        IReadOnlyList<string> labels,
        DiagnosticScenarioResult scenario)
    {
        var failure = scenario.Failure;
        var failedIndex = failure is null
            ? (int?)null
            : devices.Select((device, index) => (device, index))
                .Where(item => item.device.ID == failure.DeviceId)
                .Select(item => (int?)item.index)
                .FirstOrDefault();
        var attempts = devices.Select((device, index) =>
            new OutputOpenAttemptResult(
                device.ID,
                device.FriendlyName,
                labels[index],
                index,
                scenario.Succeeded || failedIndex is null || index < failedIndex,
                index == failedIndex ? failure?.Operation : null,
                index == failedIndex ? failure?.ErrorInfo?.FailureCategory : null,
                index == failedIndex ? failure?.ErrorInfo : null,
                "Direct (device mix format)"))
            .ToArray();
        return new OutputOrderAttemptResult(
            name,
            devices.Select(device => device.ID).ToArray(),
            scenario.Succeeded,
            failedIndex,
            failure?.DeviceId ?? string.Empty,
            failure?.Operation,
            failure?.ErrorInfo?.FailureCategory,
            failure?.ErrorInfo,
            attempts);
    }

    private static Dictionary<string, string> GetConversionModes(
        MMDevice? sourceDevice,
        MMDevice output1,
        MMDevice output2)
    {
        var modes = new Dictionary<string, string>
        {
            [output1.ID] = "Not evaluated (source unavailable)",
            [output2.ID] = "Not evaluated (source unavailable)"
        };

        if (sourceDevice is null)
        {
            return modes;
        }

        try
        {
            using var capture = new WasapiLoopbackCapture(sourceDevice);
            foreach (var output in new[] { output1, output2 })
            {
                var source = new BufferedWaveProvider(capture.WaveFormat)
                {
                    ReadFully = true,
                    DiscardOnBufferOverflow = true
                };
                using var chain = OutputProviderChain.Create(
                    source,
                    AudioFormatAdapter.GetDeviceMixFormat(output));
                modes[output.ID] = chain.ConversionMode;
            }
        }
        catch (Exception exception)
        {
            var error = AudioErrorClassifier.Classify(exception);
            modes["conversion-error"] =
                $"{error.KnownSymbolicName} {error.HResultHex}: " +
                error.OriginalMessage;
        }

        return modes;
    }

    private static string BuildTechnicalReport(
        AudioDeviceDescriptor? source,
        IReadOnlyList<AudioDeviceDescriptor> outputs,
        IReadOnlyDictionary<string, string> conversionModes,
        IReadOnlyList<DiagnosticScenarioResult> scenarios,
        CompatibilityClassification classification,
        string summary)
    {
        var builder = new StringBuilder();
        void AppendInvariant(string line) => builder.AppendLine(line);

        var appAssembly = Assembly.GetExecutingAssembly().GetName();
        var naudioAssembly = typeof(WasapiOut).Assembly.GetName();
        builder.AppendLine("MultiBluetoothAudioRouter Diagnostic Report");
        AppendInvariant($"Application version: {appAssembly.Version}");
        AppendInvariant($"Windows: {RuntimeInformation.OSDescription}");
        AppendInvariant($"Windows build/version: {Environment.OSVersion.Version}");
        AppendInvariant($".NET runtime: {RuntimeInformation.FrameworkDescription}");
        AppendInvariant($"NAudio assembly version: {naudioAssembly.Version}");
        builder.AppendLine("Source endpoint:");
        if (source is null)
        {
            builder.AppendLine("- Not selected");
        }
        else
        {
            AppendInvariant($"- {source.FriendlyName}");
            AppendInvariant($"  Device ID: {source.DeviceId}");
            AppendInvariant($"  State: {source.DeviceState}");
            AppendInvariant($"  Transport: {source.TransportKind}");
            AppendInvariant($"  Transport heuristic: " +
                source.IsTransportClassificationHeuristic);
            AppendInvariant($"  Transport evidence: " +
                source.TransportEvidence);
            AppendInvariant($"  Mix format: {source.MixFormat}");
        }
        builder.AppendLine();
        builder.AppendLine("Output endpoints:");

        foreach (var output in outputs)
        {
            AppendInvariant($"- {output.FriendlyName}");
            AppendInvariant($"  Device ID: {output.DeviceId}");
            AppendInvariant($"  State: {output.DeviceState}");
            AppendInvariant($"  Transport: {output.TransportKind}");
            AppendInvariant($"  Transport heuristic: " +
                output.IsTransportClassificationHeuristic);
            AppendInvariant($"  Transport evidence: {output.TransportEvidence}");
            AppendInvariant($"  Mix format: {output.MixFormat}");
            AppendInvariant($"  Conversion mode: " +
                conversionModes.GetValueOrDefault(output.DeviceId, "Unavailable"));
        }

        builder.AppendLine();
        builder.AppendLine("Opening tests:");
        foreach (var scenario in scenarios)
        {
            AppendInvariant($"[{scenario.DisplayName}] " +
                (scenario.Succeeded ? "SUCCESS" : "FAILED"));
            var openingOrder = scenario.Steps
                .Where(step => step.Operation == DiagnosticOperation.ReadMixFormat)
                .Select(step => $"{step.DeviceLabel} ({step.DeviceId})")
                .ToArray();
            builder.AppendLine("  Opening order: " +
                (openingOrder.Length == 0
                    ? "Unavailable"
                    : string.Join(" -> ", openingOrder)));
            foreach (var step in scenario.Steps)
            {
                AppendInvariant(
                    $"  {step.DeviceLabel} | {step.Position} | " +
                    $"{step.Operation} | {step.Status}");
                if (step.ErrorInfo is not null)
                {
                    AppendInvariant(
                        $"    Exception type: {step.ErrorInfo.ExceptionType}");
                    AppendInvariant(
                        $"    Original message: {step.ErrorInfo.OriginalMessage}");
                    AppendInvariant(
                        $"    {step.ErrorInfo.KnownSymbolicName} " +
                        $"{step.ErrorInfo.HResultHex} " +
                        $"({step.ErrorInfo.HResultSigned})");
                    AppendInvariant(
                        $"    {step.ErrorInfo.TechnicalDescription}");
                    builder.AppendLine("    Exception chain:");
                    foreach (var exception in step.ErrorInfo.InnerExceptionChain)
                    {
                        AppendInvariant(
                            $"      {exception.ExceptionType} | " +
                            $"{exception.HResultHex} ({exception.HResultSigned}) | " +
                            exception.Message);
                    }
                }
            }
        }

        builder.AppendLine();
        AppendInvariant($"Final classification: {classification}");
        AppendInvariant($"Classification is probabilistic: " +
            IsProbabilistic(classification));
        AppendInvariant($"Summary: {summary}");
        return builder.ToString();
    }

    private static string GetClassificationSummary(
        CompatibilityClassification classification) => classification switch
    {
        CompatibilityClassification.Compatible =>
            "All outputs work individually and simultaneously in both orders.",
        CompatibilityClassification.IndividualDeviceFailure =>
            "At least one output also fails when opened alone.",
        CompatibilityClassification.SecondEndpointResourceLimitLikely =>
            "Each output works alone, but the later simultaneous endpoint fails " +
            "in both orders. A shared driver/audio-stack resource limit is highly " +
            "likely, but not proven.",
        CompatibilityClassification.DeviceSpecificSimultaneousFailure =>
            "The same physical output fails in both simultaneous orders.",
        CompatibilityClassification.OrderSensitive =>
            "Only one endpoint opening order succeeds.",
        CompatibilityClassification.FormatConversionFailure =>
            "A format conversion chain failed before endpoint opening.",
        _ => "The failure pattern is inconclusive."
    };

    private static bool IsProbabilistic(
        CompatibilityClassification classification) => classification is
            CompatibilityClassification.SecondEndpointResourceLimitLikely or
            CompatibilityClassification.DeviceSpecificSimultaneousFailure or
            CompatibilityClassification.OrderSensitive or
            CompatibilityClassification.UnknownFailure;

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
                // Logging subscribers must not abort diagnostic cleanup.
            }
        }
    }
}
