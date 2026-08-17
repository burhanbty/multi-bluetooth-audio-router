using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class OutputRouteSession : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<string> _log;
    private RouteAudioBuffer? _buffer;
    private OutputProviderChain? _providerChain;
    private WasapiOut? _output;
    private DelayApplicationResult? _delayApplication;
    private int _disposeStarted;

    public OutputRouteSession(
        OutputRouteConfiguration configuration,
        Action<string> log)
    {
        Configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public OutputRouteConfiguration Configuration { get; }

    public event EventHandler<RoutePlaybackStoppedEventArgs>? PlaybackStopped;

    public void Initialize(WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            if (_output is not null || _buffer is not null || _providerChain is not null)
            {
                throw new InvalidOperationException(
                    $"Route '{Configuration.RouteId}' is already initialized.");
            }

            ValidateConfiguration();

            var buffer = new RouteAudioBuffer(
                waveFormat,
                TimeSpan.FromMilliseconds(
                    Configuration.WasapiLatencyMilliseconds +
                    Configuration.DelayMilliseconds));

            var delayApplication = AudioDelayHelper.ApplyInitialSilence(
                waveFormat,
                Configuration.DelayMilliseconds,
                buffer.WriteInitialSilence);
            LogDelayApplication(buffer, waveFormat, delayApplication);

            WaveFormat deviceMixFormat;

            try
            {
                deviceMixFormat = AudioFormatAdapter.GetDeviceMixFormat(
                    Configuration.OutputDevice);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Routing {Configuration.Label} could not read the Windows " +
                    $"mix format for {Configuration.OutputDevice.FriendlyName} " +
                    $"({Configuration.OutputDevice.ID}): {exception.Message}",
                    exception);
            }

            _log($"Route: {Configuration.Label}");
            _log($"Capture format: {waveFormat}");
            _log($"Device mix format: {deviceMixFormat}");

            OutputProviderChain providerChain;

            try
            {
                providerChain = OutputProviderChain.Create(
                    buffer,
                    deviceMixFormat);
            }
            catch (AudioFormatConversionException exception)
            {
                throw new InvalidOperationException(
                    $"Routing {Configuration.Label} format conversion setup " +
                    $"failed for {Configuration.OutputDevice.FriendlyName}: " +
                    exception.Message,
                    exception);
            }

            _log($"Conversion mode: {providerChain.ConversionMode}");
            if (providerChain.UsesConversion)
            {
                _log($"Media Foundation resampler quality: " +
                     OutputProviderChain.MediaFoundationResamplerQuality);
            }

            _log($"Opening Routing {Configuration.Label}: " +
                 Configuration.OutputDevice.FriendlyName);

            WasapiOut? output = null;

            try
            {
                // Polling mode is more compatible with some Bluetooth drivers.
                output = new WasapiOut(
                    Configuration.OutputDevice,
                    AudioClientShareMode.Shared,
                    false,
                    Configuration.WasapiLatencyMilliseconds);
                output.Init(providerChain.OutputProvider);
                output.PlaybackStopped += Output_PlaybackStopped;

                _buffer = buffer;
                _providerChain = providerChain;
                _output = output;
                _delayApplication = delayApplication;
                _log($"Routing {Configuration.Label} opened successfully.");
            }
            catch (Exception exception)
            {
                if (output is not null)
                {
                    output.PlaybackStopped -= Output_PlaybackStopped;
                    try
                    {
                        output.Dispose();
                    }
                    catch (Exception disposeException)
                    {
                        _log($"Routing {Configuration.Label} endpoint cleanup " +
                             $"after open failure also failed: " +
                             disposeException.Message);
                    }
                }

                try
                {
                    providerChain.Dispose();
                }
                catch (Exception disposeException)
                {
                    _log($"Routing {Configuration.Label} conversion cleanup " +
                         $"after endpoint failure also failed: " +
                         disposeException.Message);
                }

                throw new InvalidOperationException(
                    $"Routing {Configuration.Label} endpoint could not open " +
                    $"{Configuration.OutputDevice.FriendlyName} " +
                    $"({Configuration.OutputDevice.ID}): {exception.Message}",
                    exception);
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            (_output ?? throw new InvalidOperationException(
                $"Route '{Configuration.RouteId}' is not initialized."))
                .Play();
        }
    }

    public void Write(byte[] audioData, int offset, int count)
    {
        RouteAudioBuffer? buffer;

        lock (_sync)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }

            buffer = _buffer;
        }

        (buffer ?? throw new InvalidOperationException(
            $"Route '{Configuration.RouteId}' is not initialized."))
            .Write(audioData, offset, count);
    }

    public double CapturedAudioMilliseconds
    {
        get
        {
            lock (_sync)
            {
                return _buffer?.CapturedAudioMilliseconds ?? 0;
            }
        }
    }

    public OutputRouteSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var buffer = _buffer;
            var providerChain = _providerChain;
            var output = _output;
            var delay = _delayApplication;
            var metrics = buffer?.GetMetrics();

            return new OutputRouteSnapshot(
                Configuration.RouteId,
                Configuration.Label,
                Configuration.OutputDevice.FriendlyName,
                Configuration.DelayMilliseconds,
                delay?.AppliedDelayMilliseconds ?? 0,
                metrics?.InitialSilenceBytes ?? 0,
                providerChain?.SourceFormat.ToString() ?? "Unavailable",
                providerChain?.TargetFormat.ToString() ?? "Unavailable",
                providerChain?.ConversionMode ?? "Unavailable",
                metrics?.BufferedMilliseconds ?? 0,
                metrics?.CapacityMilliseconds ?? 0,
                metrics?.TotalWrittenBytes ?? 0,
                metrics?.TotalReadBytes ?? 0,
                metrics?.UnderflowCount ?? 0,
                metrics?.OverflowCount ?? 0,
                metrics?.EstimatedDroppedBytes ?? 0,
                output?.PlaybackState.ToString() ?? "Stopped");
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Configuration.RouteId))
        {
            throw new ArgumentException("RouteId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Configuration.Label))
        {
            throw new ArgumentException("Output route label cannot be empty.");
        }

        if (Configuration.DelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Configuration.DelayMilliseconds));
        }

        if (Configuration.WasapiLatencyMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Configuration.WasapiLatencyMilliseconds));
        }
    }

    private void LogDelayApplication(
        RouteAudioBuffer buffer,
        WaveFormat waveFormat,
        DelayApplicationResult delay)
    {
        _log($"{Configuration.Label} device: " +
             Configuration.OutputDevice.FriendlyName);
        _log($"{Configuration.Label} requested DelayMs: " +
             delay.RequestedDelayMilliseconds);
        _log($"{Configuration.Label} WaveFormat: {waveFormat}");
        _log($"{Configuration.Label} AverageBytesPerSecond: " +
             waveFormat.AverageBytesPerSecond);
        _log($"{Configuration.Label} BlockAlign: {waveFormat.BlockAlign}");
        _log($"{Configuration.Label} calculated silenceBytes: " +
             delay.CalculatedBytes);
        _log($"{Configuration.Label} final aligned silenceBytes: " +
             delay.AlignedBytes);
        _log($"{Configuration.Label} applied delay: " +
             $"{delay.AppliedDelayMilliseconds:F2} ms.");

        if (delay.AlignedBytes > 0)
        {
            _log($"{Configuration.Label}: silence was written to this output buffer.");
        }
        else
        {
            _log($"{Configuration.Label} warning: inserted delay is zero; " +
                 "no silence was written.");
        }

        if (buffer.InitialSilenceBytes < delay.AlignedBytes)
        {
            _log($"{Configuration.Label} warning: delay buffer was not applied " +
                 "as expected.");
        }
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        PlaybackStopped?.Invoke(
            this,
            new RoutePlaybackStoppedEventArgs(Configuration, e.Exception));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        WasapiOut? output;
        OutputProviderChain? providerChain;

        lock (_sync)
        {
            output = _output;
            providerChain = _providerChain;
            _output = null;
            _providerChain = null;
            _buffer = null;
            _delayApplication = null;
        }

        if (output is not null)
        {
            output.PlaybackStopped -= Output_PlaybackStopped;

            try
            {
                output.Stop();
            }
            catch (Exception exception)
            {
                _log($"Routing {Configuration.Label} could not be stopped cleanly: " +
                     exception.Message);
            }

            try
            {
                output.Dispose();
            }
            catch (Exception exception)
            {
                _log($"Routing {Configuration.Label} could not be disposed cleanly: " +
                     exception.Message);
            }
        }

        if (providerChain is not null)
        {
            try
            {
                providerChain.Dispose();
            }
            catch (Exception exception)
            {
                _log($"Routing {Configuration.Label} conversion chain could not " +
                     $"be disposed cleanly: {exception.Message}");
            }
        }
    }
}

public sealed class RoutePlaybackStoppedEventArgs : EventArgs
{
    public RoutePlaybackStoppedEventArgs(
        OutputRouteConfiguration configuration,
        Exception? exception)
    {
        Configuration = configuration;
        Exception = exception;
    }

    public OutputRouteConfiguration Configuration { get; }

    public Exception? Exception { get; }
}
