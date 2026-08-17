using System.Diagnostics.CodeAnalysis;
using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed record EndpointTestTarget(string Label, MMDevice Device);

public sealed class AudioEndpointTestRunner
{
    public const int DiagnosticLatencyMilliseconds = 300;

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The runner is an injected service boundary for hardware diagnostics.")]
    public Task<DiagnosticScenarioResult> RunScenarioAsync(
        string scenarioId,
        string scenarioLabel,
        IReadOnlyList<EndpointTestTarget> orderedTargets,
        int holdDurationMilliseconds,
        bool detailedLogging,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (orderedTargets.Count == 0)
        {
            throw new ArgumentException(
                "At least one endpoint target is required.",
                nameof(orderedTargets));
        }

        return Task.Run(
            () => RunScenarioCoreAsync(
                scenarioId,
                scenarioLabel,
                orderedTargets,
                holdDurationMilliseconds,
                detailedLogging,
                log,
                cancellationToken),
            cancellationToken);
    }

    private static async Task<DiagnosticScenarioResult> RunScenarioCoreAsync(
        string scenarioId,
        string scenarioLabel,
        IReadOnlyList<EndpointTestTarget> orderedTargets,
        int holdDurationMilliseconds,
        bool detailedLogging,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        void WriteLog(string message)
        {
            if (detailedLogging)
            {
                log?.Invoke(message);
            }
        }

        WriteLog($"--- {scenarioLabel} ---");
        WriteLog("Opening order: " +
                 string.Join(" -> ", orderedTargets.Select(target => target.Label)));

        var builder = new ScenarioBuilder(
            scenarioId,
            scenarioLabel,
            WriteLog);
        var contexts = orderedTargets
            .Select((target, index) => new EndpointContext(
                target.Label,
                target.Device,
                GetPosition(index, orderedTargets.Count),
                index))
            .ToArray();
        var workflowSucceeded = false;

        try
        {
            var allOpened = true;
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!builder.TryReadMixFormat(context) ||
                    !builder.TryCreateOutput(context) ||
                    !builder.TryInitialize(context))
                {
                    allOpened = false;
                    break;
                }
            }

            if (allOpened)
            {
                foreach (var context in contexts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!builder.TryPlay(context))
                    {
                        allOpened = false;
                        break;
                    }
                }
            }

            if (allOpened)
            {
                workflowSucceeded = await builder.TryHoldOpenAsync(
                        contexts,
                        holdDurationMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var context in contexts.Reverse())
            {
                builder.Cleanup(context);
            }
        }

        return builder.Build(workflowSucceeded);
    }

    private static DiagnosticEndpointPosition GetPosition(int index, int count)
    {
        if (count == 1)
        {
            return DiagnosticEndpointPosition.OnlyEndpoint;
        }

        return index switch
        {
            0 => DiagnosticEndpointPosition.First,
            1 => DiagnosticEndpointPosition.Second,
            _ => DiagnosticEndpointPosition.Subsequent
        };
    }

    private sealed class EndpointContext
    {
        public EndpointContext(
            string label,
            MMDevice device,
            DiagnosticEndpointPosition position,
            int orderIndex)
        {
            Label = label;
            Device = device;
            Position = position;
            OrderIndex = orderIndex;
        }

        public string Label { get; }
        public MMDevice Device { get; }
        public DiagnosticEndpointPosition Position { get; }
        public int OrderIndex { get; }
        public WaveFormat? MixFormat { get; set; }
        public BufferedWaveProvider? Provider { get; set; }
        public WasapiOut? Output { get; set; }
        public bool Initialized { get; set; }
        public TaskCompletionSource<StoppedEventArgs> PlaybackStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<StoppedEventArgs>? PlaybackStoppedHandler { get; set; }
    }

    private sealed class ScenarioBuilder
    {
        private readonly string _scenarioId;
        private readonly string _scenarioLabel;
        private readonly Action<string> _log;
        private readonly List<DiagnosticStepResult> _steps = [];

        public ScenarioBuilder(
            string scenarioId,
            string scenarioLabel,
            Action<string> log)
        {
            _scenarioId = scenarioId;
            _scenarioLabel = scenarioLabel;
            _log = log;
        }

        public bool TryReadMixFormat(EndpointContext endpoint) => TryStep(
            endpoint,
            DiagnosticOperation.ReadMixFormat,
            () =>
            {
                endpoint.MixFormat =
                    AudioFormatAdapter.GetDeviceMixFormat(endpoint.Device);
                var format = endpoint.MixFormat;
                return $"Mix format: {format}; SampleRate={format.SampleRate}; " +
                       $"Channels={format.Channels}; " +
                       $"BitsPerSample={format.BitsPerSample}; " +
                       $"Encoding={format.Encoding}.";
            });

        public bool TryCreateOutput(EndpointContext endpoint) => TryStep(
            endpoint,
            DiagnosticOperation.CreateWasapiOut,
            () =>
            {
                endpoint.Output = new WasapiOut(
                    endpoint.Device,
                    AudioClientShareMode.Shared,
                    false,
                    DiagnosticLatencyMilliseconds);
                endpoint.PlaybackStoppedHandler = (_, eventArgs) =>
                    endpoint.PlaybackStopped.TrySetResult(eventArgs);
                endpoint.Output.PlaybackStopped += endpoint.PlaybackStoppedHandler;
                return "WasapiOut created in shared polling mode.";
            });

        public bool TryInitialize(EndpointContext endpoint) => TryStep(
            endpoint,
            DiagnosticOperation.Initialize,
            () =>
            {
                var format = endpoint.MixFormat ??
                    throw new InvalidOperationException("Mix format is unavailable.");
                var output = endpoint.Output ??
                    throw new InvalidOperationException("WasapiOut is unavailable.");
                endpoint.Provider = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                output.Init(endpoint.Provider);
                endpoint.Initialized = true;
                return "WasapiOut.Init completed with the endpoint mix format.";
            });

        public bool TryPlay(EndpointContext endpoint) => TryStep(
            endpoint,
            DiagnosticOperation.Play,
            () =>
            {
                (endpoint.Output ?? throw new InvalidOperationException(
                    "WasapiOut is unavailable.")).Play();
                return "Silent diagnostic playback started.";
            });

        public async Task<bool> TryHoldOpenAsync(
            IReadOnlyList<EndpointContext> endpoints,
            int holdDurationMilliseconds,
            CancellationToken cancellationToken)
        {
            var holdTask = Task.Delay(
                holdDurationMilliseconds,
                cancellationToken);
            var waitTasks = endpoints
                .Select(endpoint => (Task)endpoint.PlaybackStopped.Task)
                .Append(holdTask)
                .ToArray();
            var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(completed, holdTask))
            {
                AddSuccess(
                    endpoints[0],
                    DiagnosticOperation.HoldOpen,
                    $"Endpoint(s) remained open for {holdDurationMilliseconds} ms.",
                    endpoints.Count == 1
                        ? endpoints[0].Position
                        : DiagnosticEndpointPosition.Both);
                return true;
            }

            var stopped = endpoints.First(endpoint =>
                ReferenceEquals(endpoint.PlaybackStopped.Task, completed));
            var eventArgs = await stopped.PlaybackStopped.Task.ConfigureAwait(false);
            AddFailure(
                stopped,
                DiagnosticOperation.HoldOpen,
                eventArgs.Exception ?? new InvalidOperationException(
                    $"{stopped.Label} stopped unexpectedly."));
            return false;
        }

        public void Cleanup(EndpointContext endpoint)
        {
            var output = endpoint.Output;
            if (output is null)
            {
                AddSkipped(endpoint, DiagnosticOperation.Stop,
                    "Stop skipped because WasapiOut was not created.");
                AddSkipped(endpoint, DiagnosticOperation.Dispose,
                    "Dispose skipped because WasapiOut was not created.");
                return;
            }

            if (endpoint.PlaybackStoppedHandler is not null)
            {
                output.PlaybackStopped -= endpoint.PlaybackStoppedHandler;
                endpoint.PlaybackStoppedHandler = null;
            }

            if (endpoint.Initialized)
            {
                TryCleanupStep(
                    endpoint,
                    DiagnosticOperation.Stop,
                    output.Stop,
                    "WasapiOut stopped cleanly.");
            }
            else
            {
                AddSkipped(endpoint, DiagnosticOperation.Stop,
                    "Stop skipped because Init did not complete.");
            }

            TryCleanupStep(
                endpoint,
                DiagnosticOperation.Dispose,
                output.Dispose,
                "WasapiOut disposed cleanly.");
            endpoint.Output = null;
            endpoint.Provider = null;
        }

        public DiagnosticScenarioResult Build(bool workflowSucceeded) => new(
            _scenarioId,
            _scenarioLabel,
            workflowSucceeded && _steps.All(step => !step.Failed),
            _steps.ToArray());

        private bool TryStep(
            EndpointContext endpoint,
            DiagnosticOperation operation,
            Func<string> action)
        {
            _log($"[{_scenarioLabel}] #{endpoint.OrderIndex + 1} " +
                 $"{endpoint.Label}: {operation}.");
            try
            {
                AddSuccess(endpoint, operation, action());
                return true;
            }
            catch (Exception exception)
            {
                AddFailure(endpoint, operation, exception);
                return false;
            }
        }

        private void TryCleanupStep(
            EndpointContext endpoint,
            DiagnosticOperation operation,
            Action action,
            string successMessage)
        {
            try
            {
                action();
                AddSuccess(endpoint, operation, successMessage);
            }
            catch (Exception exception)
            {
                AddFailure(endpoint, operation, exception);
            }
        }

        private void AddSuccess(
            EndpointContext endpoint,
            DiagnosticOperation operation,
            string message,
            DiagnosticEndpointPosition? positionOverride = null)
        {
            _steps.Add(CreateResult(
                endpoint,
                positionOverride ?? endpoint.Position,
                operation,
                DiagnosticStepStatus.Success,
                message,
                [],
                null));
            _log($"[{_scenarioLabel}] {operation}: SUCCESS - {message}");
        }

        private void AddSkipped(
            EndpointContext endpoint,
            DiagnosticOperation operation,
            string message)
        {
            _steps.Add(CreateResult(
                endpoint,
                endpoint.Position,
                operation,
                DiagnosticStepStatus.Skipped,
                message,
                [],
                null));
            _log($"[{_scenarioLabel}] {operation}: SKIPPED - {message}");
        }

        private void AddFailure(
            EndpointContext endpoint,
            DiagnosticOperation operation,
            Exception exception)
        {
            var error = AudioErrorClassifier.Classify(exception);
            var details = error.InnerExceptionChain.Select(item =>
                new DiagnosticExceptionDetail(
                    item.ExceptionType,
                    item.Message,
                    item.HResultSigned,
                    item.HResultHex)).ToArray();
            var message = $"{operation} failed for {endpoint.Label}: " +
                          $"{error.KnownSymbolicName} {error.HResultHex} - " +
                          error.UserFacingDescription;
            _steps.Add(CreateResult(
                endpoint,
                endpoint.Position,
                operation,
                DiagnosticStepStatus.Failed,
                message,
                details,
                error));
            _log($"[{_scenarioLabel}] FAILED at {operation} for " +
                 $"#{endpoint.OrderIndex + 1} {endpoint.Label}.");
            _log($"{error.KnownSymbolicName}: {error.HResultHex} " +
                 $"({error.HResultSigned})");
            _log(error.UserFacingDescription);
            foreach (var item in error.InnerExceptionChain)
            {
                _log($"{item.ExceptionType}: {item.HResultHex} - {item.Message}");
            }
        }

        private DiagnosticStepResult CreateResult(
            EndpointContext endpoint,
            DiagnosticEndpointPosition position,
            DiagnosticOperation operation,
            DiagnosticStepStatus status,
            string message,
            IReadOnlyList<DiagnosticExceptionDetail> exceptions,
            AudioErrorInfo? errorInfo) => new(
                _scenarioId,
                _scenarioLabel,
                endpoint.Label,
                endpoint.Device.FriendlyName,
                endpoint.Device.ID,
                position,
                operation,
                status,
                message,
                exceptions,
                errorInfo);
    }
}
