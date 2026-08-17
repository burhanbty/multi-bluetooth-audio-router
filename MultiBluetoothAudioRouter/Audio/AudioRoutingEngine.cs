using System.Diagnostics;
using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class AudioRoutingEngine : IDisposable
{
    // 150 ms absorbs normal scheduler jitter without adding a large startup
    // penalty. It is common to every route, so relative manual delay is kept.
    public const int BasePrebufferMilliseconds = 150;
    public const int PrebufferTimeoutMilliseconds = 2000;
    private const int PrebufferPollMilliseconds = 10;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WasapiLoopbackCapture? _capture;
    private List<OutputRouteSession> _routes = [];
    private CancellationTokenSource? _startupCancellation;
    private RoutingState _state = RoutingState.Stopped;
    private bool _failureStopQueued;
    private bool _startupFaulted;
    private long _runGeneration;

    public event Action<string>? LogMessage;

    public event Action<RoutingState>? StateChanged;

    public RoutingState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsActive => State is
        RoutingState.Starting or
        RoutingState.Running or
        RoutingState.Stopping or
        RoutingState.Faulted;

    public async Task StartAsync(
        MMDevice sourceDevice,
        IEnumerable<OutputRouteConfiguration> routeConfigurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);
        ArgumentNullException.ThrowIfNull(routeConfigurations);

        var configurations = routeConfigurations.ToList();
        ValidateConfigurations(sourceDevice, configurations);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        WasapiLoopbackCapture? capture = null;
        var openedRoutes = new List<OutputRouteSession>();
        CancellationTokenSource? startupCancellation = null;
        long runGeneration = 0;

        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                if (_state != RoutingState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Routing cannot start while the engine state is {_state}.");
                }

                _failureStopQueued = false;
                _startupFaulted = false;
                runGeneration = ++_runGeneration;
                startupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                _startupCancellation = startupCancellation;
                SetStateLocked(RoutingState.Starting);
            }

            var startupToken = startupCancellation.Token;
            startupToken.ThrowIfCancellationRequested();

            capture = new WasapiLoopbackCapture(sourceDevice);

            foreach (var configuration in configurations)
            {
                startupToken.ThrowIfCancellationRequested();

                var route = new OutputRouteSession(configuration, PublishLog);
                route.PlaybackStopped += Route_PlaybackStopped;

                try
                {
                    route.Initialize(capture.WaveFormat);
                    openedRoutes.Add(route);
                }
                catch
                {
                    route.PlaybackStopped -= Route_PlaybackStopped;
                    route.Dispose();
                    throw;
                }
            }

            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += Capture_RecordingStopped;

            EnsureStartupCanContinue(runGeneration, startupToken);

            lock (_sync)
            {
                if (runGeneration != _runGeneration ||
                    _state != RoutingState.Starting)
                {
                    throw new OperationCanceledException(startupToken);
                }

                _capture = capture;
                _routes = openedRoutes;
            }

            PublishLog(
                $"Source Device: {sourceDevice.FriendlyName} ({sourceDevice.ID})");
            foreach (var route in openedRoutes)
            {
                PublishLog($"{route.Configuration.Label}: " +
                           $"{route.Configuration.OutputDevice.FriendlyName} " +
                           $"({route.Configuration.OutputDevice.ID})");
            }

            PublishLog($"Capture format: {capture.WaveFormat}");
            PublishLog(
                $"Starting capture and waiting for a common " +
                $"{BasePrebufferMilliseconds} ms prebuffer.");

            capture.StartRecording();

            var prebufferReady = await WaitForPrebufferAsync(
                    openedRoutes,
                    startupToken)
                .ConfigureAwait(false);

            EnsureStartupCanContinue(runGeneration, startupToken);

            if (prebufferReady)
            {
                PublishLog(
                    $"Common {BasePrebufferMilliseconds} ms prebuffer is ready.");
            }
            else
            {
                PublishLog(
                    $"Warning: capture did not reach the common " +
                    $"{BasePrebufferMilliseconds} ms prebuffer within " +
                    $"{PrebufferTimeoutMilliseconds} ms. Outputs will start " +
                    "to avoid blocking routing indefinitely.");
            }

            foreach (var route in openedRoutes)
            {
                PublishLog(
                    $"{route.Configuration.Label} captured prebuffer: " +
                    $"{route.CapturedAudioMilliseconds:F1} ms; manual delay: " +
                    $"{route.Configuration.DelayMilliseconds} ms.");
            }

            // Start calls are deliberately adjacent so all initialized outputs
            // begin consuming their common prebuffer as closely as possible.
            foreach (var route in openedRoutes)
            {
                route.Start();
            }

            EnsureStartupCanContinue(runGeneration, startupToken);

            lock (_sync)
            {
                if (runGeneration != _runGeneration ||
                    _state != RoutingState.Starting)
                {
                    throw new OperationCanceledException(startupToken);
                }

                _startupCancellation = null;
                SetStateLocked(RoutingState.Running);
            }

            PublishLog("Routing started.");
        }
        catch (OperationCanceledException)
        {
            var finalState = GetStartupFailureState(runGeneration);
            RollBackStartup(capture, openedRoutes, runGeneration, finalState);
            PublishLog(finalState == RoutingState.Faulted
                ? "Routing startup failed and entered Faulted state."
                : "Routing startup was cancelled.");
            throw;
        }
        catch
        {
            RollBackStartup(
                capture,
                openedRoutes,
                runGeneration,
                RoutingState.Faulted);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_startupCancellation, startupCancellation))
                {
                    _startupCancellation = null;
                }
            }

            startupCancellation?.Dispose();
            _lifecycleGate.Release();
        }
    }

    public void Stop()
    {
        StopCore(logStopped: true, RoutingState.Stopped);
    }

    public void Stop(bool logMessage)
    {
        StopCore(logMessage, RoutingState.Stopped);
    }

    public IReadOnlyList<OutputRouteSnapshot> GetRouteSnapshots()
    {
        OutputRouteSession[] routes;

        lock (_sync)
        {
            routes = _routes.ToArray();
        }

        return routes.Select(route => route.GetSnapshot()).ToArray();
    }

    private static async Task<bool> WaitForPrebufferAsync(
        IReadOnlyCollection<OutputRouteSession> routes,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < PrebufferTimeoutMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (routes.All(route =>
                    route.CapturedAudioMilliseconds >= BasePrebufferMilliseconds))
            {
                return true;
            }

            await Task.Delay(PrebufferPollMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        IReadOnlyList<OutputRouteSession> routes;

        lock (_sync)
        {
            if (_state is not RoutingState.Starting and not RoutingState.Running ||
                !ReferenceEquals(sender, _capture))
            {
                return;
            }

            // Route lists are replaced as a whole and never mutated while active,
            // so the audio callback can iterate this snapshot without allocation.
            routes = _routes;
        }

        try
        {
            foreach (var route in routes)
            {
                route.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }
        catch (Exception exception)
        {
            RequestFailureStop(
                "Could not write captured audio to the output buffers: " +
                exception.Message,
                sender);
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        var message = e.Exception is null
            ? "Loopback capture stopped unexpectedly."
            : $"Loopback capture failed: {e.Exception.Message}";

        RequestFailureStop(message, sender);
    }

    private void Route_PlaybackStopped(
        object? sender,
        RoutePlaybackStoppedEventArgs e)
    {
        var message = e.Exception is null
            ? $"{e.Configuration.Label} stopped unexpectedly."
            : $"{e.Configuration.Label} failed: {e.Exception.Message}";

        RequestFailureStop(message, sender);
    }

    private void RequestFailureStop(string message, object? failureSource)
    {
        long failedRunGeneration;
        CancellationTokenSource? startupCancellation = null;

        lock (_sync)
        {
            if (failureSource is WasapiLoopbackCapture capture &&
                !ReferenceEquals(capture, _capture))
            {
                return;
            }

            if (failureSource is OutputRouteSession route &&
                !_routes.Contains(route))
            {
                return;
            }

            if (_state is not RoutingState.Starting and not RoutingState.Running ||
                _failureStopQueued)
            {
                return;
            }

            _failureStopQueued = true;
            failedRunGeneration = _runGeneration;

            if (_state == RoutingState.Starting)
            {
                _startupFaulted = true;
                startupCancellation = _startupCancellation;
            }
        }

        PublishLog(message);

        if (startupCancellation is not null)
        {
            TryCancel(startupCancellation);
            return;
        }

        _ = Task.Run(() => StopCore(
            logStopped: false,
            RoutingState.Faulted,
            failedRunGeneration));
    }

    private void StopCore(
        bool logStopped,
        RoutingState finalState,
        long? expectedRunGeneration = null)
    {
        CancellationTokenSource? startupCancellation;
        bool stopWasRequested;

        lock (_sync)
        {
            if (expectedRunGeneration.HasValue &&
                expectedRunGeneration.Value != _runGeneration)
            {
                return;
            }

            if (expectedRunGeneration.HasValue &&
                _state is not RoutingState.Starting and not RoutingState.Running)
            {
                return;
            }

            if (_state == RoutingState.Disposed)
            {
                return;
            }

            stopWasRequested = _state != RoutingState.Stopped;
            startupCancellation = _startupCancellation;

            if (stopWasRequested && _state != RoutingState.Stopping)
            {
                SetStateLocked(RoutingState.Stopping);
            }
        }

        TryCancel(startupCancellation);
        _lifecycleGate.Wait();

        try
        {
            WasapiLoopbackCapture? capture;
            List<OutputRouteSession> routes;

            lock (_sync)
            {
                if (expectedRunGeneration.HasValue &&
                    expectedRunGeneration.Value != _runGeneration)
                {
                    return;
                }

                if (expectedRunGeneration.HasValue &&
                    _state is RoutingState.Stopped or RoutingState.Faulted)
                {
                    return;
                }

                if (_state == RoutingState.Disposed)
                {
                    return;
                }

                capture = _capture;
                routes = _routes;
                _capture = null;
                _routes = [];
                _failureStopQueued = false;
                _startupFaulted = false;
            }

            CleanUpResources(capture, routes);

            lock (_sync)
            {
                if (_state != RoutingState.Disposed)
                {
                    SetStateLocked(finalState);
                }
            }

            if (logStopped && stopWasRequested)
            {
                PublishLog("Routing stopped.");
            }

            if (finalState == RoutingState.Faulted)
            {
                PublishLog("Routing stopped after a fault. Engine state: Faulted.");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private RoutingState GetStartupFailureState(long runGeneration)
    {
        lock (_sync)
        {
            return runGeneration == _runGeneration && _startupFaulted
                ? RoutingState.Faulted
                : RoutingState.Stopped;
        }
    }

    private void EnsureStartupCanContinue(
        long runGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (runGeneration != _runGeneration ||
                _state != RoutingState.Starting)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Startup already completed its CTS lifecycle.
        }
    }

    private void RollBackStartup(
        WasapiLoopbackCapture? capture,
        List<OutputRouteSession> routes,
        long runGeneration,
        RoutingState finalState)
    {
        lock (_sync)
        {
            if (runGeneration == _runGeneration)
            {
                _capture = null;
                _routes = [];
            }
        }

        CleanUpResources(capture, routes);

        lock (_sync)
        {
            if (_state != RoutingState.Disposed &&
                runGeneration == _runGeneration)
            {
                _failureStopQueued = false;
                _startupFaulted = false;
                SetStateLocked(finalState);
            }
        }
    }

    private void CleanUpResources(
        WasapiLoopbackCapture? capture,
        IEnumerable<OutputRouteSession> routes)
    {
        if (capture is not null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            StopAndDisposeCapture(capture);
        }

        DisposeRoutes(routes);
    }

    private void DisposeRoutes(IEnumerable<OutputRouteSession> routes)
    {
        foreach (var route in routes.Reverse())
        {
            route.PlaybackStopped -= Route_PlaybackStopped;
            route.Dispose();
        }
    }

    private void StopAndDisposeCapture(WasapiLoopbackCapture capture)
    {
        try
        {
            capture.StopRecording();
        }
        catch (Exception exception)
        {
            PublishLog("Loopback capture could not be stopped cleanly: " +
                       exception.Message);
        }

        try
        {
            capture.Dispose();
        }
        catch (Exception exception)
        {
            PublishLog("Loopback capture could not be disposed cleanly: " +
                       exception.Message);
        }
    }

    private static void ValidateConfigurations(
        MMDevice sourceDevice,
        List<OutputRouteConfiguration> configurations)
    {
        if (configurations.Count == 0)
        {
            throw new ArgumentException(
                "At least one output route is required.",
                nameof(configurations));
        }

        if (configurations.Any(configuration => configuration is null))
        {
            throw new ArgumentException(
                "Output route configurations cannot contain null entries.",
                nameof(configurations));
        }

        var duplicateRouteId = configurations
            .GroupBy(configuration => configuration.RouteId,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;

        if (duplicateRouteId is not null)
        {
            throw new ArgumentException(
                $"Duplicate route ID: {duplicateRouteId}.",
                nameof(configurations));
        }

        var duplicateDevice = configurations
            .GroupBy(configuration => configuration.OutputDevice.ID,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateDevice is not null)
        {
            throw new ArgumentException(
                "Multiple routes cannot use the same output device.",
                nameof(configurations));
        }

        if (configurations.Any(configuration =>
                configuration.OutputDevice.ID == sourceDevice.ID))
        {
            throw new ArgumentException(
                "The source device cannot also be used as an output device.",
                nameof(configurations));
        }
    }

    private void SetStateLocked(RoutingState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        PublishState(state);
    }

    private void PublishLog(string message)
    {
        foreach (Action<string> handler in
                 LogMessage?.GetInvocationList() ?? [])
        {
            try
            {
                handler(message);
            }
            catch
            {
                // A logging subscriber must not terminate the audio engine.
            }
        }
    }

    private void PublishState(RoutingState state)
    {
        foreach (Action<RoutingState> handler in
                 StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(state);
            }
            catch
            {
                // A state subscriber must not terminate the audio engine.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_state == RoutingState.Disposed, this);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_state == RoutingState.Disposed)
            {
                return;
            }
        }

        StopCore(logStopped: false, RoutingState.Stopped);

        lock (_sync)
        {
            SetStateLocked(RoutingState.Disposed);
            LogMessage = null;
            StateChanged = null;
        }
    }
}
