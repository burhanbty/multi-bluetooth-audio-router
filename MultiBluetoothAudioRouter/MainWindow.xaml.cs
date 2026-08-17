using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiBluetoothAudioRouter.Audio;
using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Window lifecycle; OnClosed disposes every owned resource.")]
public partial class MainWindow : Window
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private readonly AudioRoutingEngine _routingEngine = new();
    private readonly AudioEndpointTestRunner _endpointTestRunner;
    private readonly AudioDeviceInspector _audioDeviceInspector;
    private readonly HardwareDiagnosticService _hardwareDiagnosticService;
    private readonly RoutingCompatibilityService _compatibilityService;
    private readonly DispatcherTimer _routingTelemetryTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private readonly Dictionary<string, (long Underflows, long Overflows)>
        _lastRouteWarningCounts = [];
    private List<MMDevice> _devices = [];
    private bool _isRefreshingDevices;
    private bool _isStoppingFileTest;
    private AudioFileReader? _fileReader1;
    private AudioFileReader? _fileReader2;
    private OutputProviderChain? _fileProviderChain1;
    private OutputProviderChain? _fileProviderChain2;
    private WasapiOut? _fileOutput1;
    private WasapiOut? _fileOutput2;
    private bool _isRunningHardwareDiagnostic;
    private bool _isRunningCompatibilityPreflight;
    private CancellationTokenSource? _compatibilityCancellation;
    private CancellationTokenSource? _diagnosticCancellation;
    private Task<RoutingCompatibilityReport>? _activeCompatibilityTask;
    private Task<HardwareDiagnosticReport>? _activeDiagnosticTask;
    private string? _lastDiagnosticReport;
    private int _deviceRefreshVersion;
    private bool _isStoppingCalibrationTest;
    private CancellationTokenSource? _calibrationCancellation;
    private BufferedWaveProvider? _calibrationBuffer1;
    private BufferedWaveProvider? _calibrationBuffer2;
    private WasapiOut? _calibrationOutput1;
    private WasapiOut? _calibrationOutput2;

    public MainWindow()
    {
        _endpointTestRunner = new AudioEndpointTestRunner();
        _audioDeviceInspector = new AudioDeviceInspector();
        _hardwareDiagnosticService = new HardwareDiagnosticService(
            _endpointTestRunner,
            _audioDeviceInspector);
        _compatibilityService = new RoutingCompatibilityService(
            _endpointTestRunner,
            _audioDeviceInspector);

        InitializeComponent();

        _routingEngine.LogMessage += RoutingEngine_LogMessage;
        _routingEngine.StateChanged += RoutingEngine_StateChanged;
        _hardwareDiagnosticService.LogMessage += HardwareDiagnosticService_LogMessage;
        _compatibilityService.LogMessage += CompatibilityService_LogMessage;
        _routingTelemetryTimer.Tick += RoutingTelemetryTimer_Tick;
        _routingTelemetryTimer.Start();

        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            RefreshDevices();
        }
        catch (Exception exception)
        {
            AppendLog($"Could not initialize the Windows audio device enumerator: {exception.Message}");
        }

        UpdateControlState();
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None ||
            e.OriginalSource is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (e.Key == Key.PageDown)
        {
            MainContentScrollViewer.PageDown();
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            MainContentScrollViewer.PageUp();
            e.Handled = true;
        }
    }

    private async void StartRoutingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRoutingDevices(
                out var sourceDevice,
                out var outputDevice1,
                out var outputDevice2))
        {
            return;
        }

        if (!TryGetRoutingDelays(out var output1DelayMs, out var output2DelayMs))
        {
            return;
        }

        StopFileTest(logMessage: IsFileTestActive());

        var routeConfigurations = new[]
        {
            new OutputRouteConfiguration(
                "output-1",
                "Output Device 1",
                outputDevice1,
                output1DelayMs),
            new OutputRouteConfiguration(
                "output-2",
                "Output Device 2",
                outputDevice2,
                output2DelayMs)
        };

        try
        {
            if (EnableFastPreflightCheckBox.IsChecked == true)
            {
                var compatibilityReport = await RunFastPreflightAsync(
                    sourceDevice,
                    routeConfigurations);

                if (compatibilityReport is null ||
                    !compatibilityReport.IsCompatible)
                {
                    return;
                }
            }

            LogSourceSetup(sourceDevice);

            await _routingEngine.StartAsync(
                sourceDevice,
                routeConfigurations);

            UpdateControlState();
        }
        catch (OperationCanceledException)
        {
            AppendLog(_routingEngine.State == RoutingState.Faulted
                ? "Routing startup failed. Review the preceding audio error."
                : "Routing startup was cancelled.");
        }
        catch (Exception exception)
        {
            var error = AudioErrorClassifier.Classify(exception);
            AppendLog($"Routing could not start: {error.KnownSymbolicName} " +
                      $"{error.HResultHex} - {error.UserFacingDescription}");
            AppendLog("Check that all selected devices are connected, active, and available to Windows.");
        }
    }

    private void StopRoutingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunningCompatibilityPreflight)
        {
            _compatibilityCancellation?.Cancel();
            AppendLog("Compatibility preflight cancellation requested.");
            return;
        }

        StopRouting(logMessage: true);
    }

    private async Task<RoutingCompatibilityReport?> RunFastPreflightAsync(
        MMDevice sourceDevice,
        IReadOnlyList<OutputRouteConfiguration> outputs)
    {
        _compatibilityCancellation?.Dispose();
        _compatibilityCancellation = new CancellationTokenSource();
        _isRunningCompatibilityPreflight = true;
        UpdateControlState();
        CompatibilityAssessmentTextBlock.Text =
            "Compatibility: Testing selected outputs...";

        try
        {
            _activeCompatibilityTask = _compatibilityService.CheckAsync(
                sourceDevice,
                outputs,
                _deviceRefreshVersion,
                _compatibilityCancellation.Token);
            var report = await _activeCompatibilityTask;
            ShowCompatibilityReport(report);

            if (!report.IsCompatible)
            {
                AppendLog("Routing was not started because fast compatibility " +
                          $"preflight returned {report.Classification}.");
                AppendLog("Run Full Hardware Diagnostic for detailed steps.");
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            CompatibilityAssessmentTextBlock.Text =
                "Compatibility: Test cancelled.";
            AppendLog("Compatibility preflight was cancelled.");
            return null;
        }
        catch (Exception exception)
        {
            var error = AudioErrorClassifier.Classify(exception);
            CompatibilityAssessmentTextBlock.Text =
                $"Compatibility: Unknown failure - {error.KnownSymbolicName}.";
            AppendLog($"Compatibility preflight failed: " +
                      $"{error.KnownSymbolicName} {error.HResultHex} - " +
                      error.UserFacingDescription);
            return null;
        }
        finally
        {
            _isRunningCompatibilityPreflight = false;
            _activeCompatibilityTask = null;
            _compatibilityCancellation?.Dispose();
            _compatibilityCancellation = null;
            UpdateControlState();
        }
    }

    private void ShowCompatibilityReport(RoutingCompatibilityReport report)
    {
        var cacheText = report.WasServedFromCache ? " (cached)" : string.Empty;
        CompatibilityAssessmentTextBlock.Text =
            $"Compatibility: {GetCompatibilityDisplayName(report.Classification)}" +
            $"{cacheText}{Environment.NewLine}" +
            report.Summary;

        if (report.Classification ==
            CompatibilityClassification.SecondEndpointResourceLimitLikely)
        {
            var includesBluetooth = report.OutputDevices.Any(device =>
                device.TransportKind is
                    AudioDeviceTransportKind.BluetoothClassicOrUnknownBluetooth or
                    AudioDeviceTransportKind.BluetoothLeAudioCandidate);
            if (includesBluetooth)
            {
                AppendLog("Suggested checks: try Bluetooth + wired/USB, update " +
                          "Bluetooth/audio drivers, and close Hands-Free/microphone apps.");
                AppendLog("If all devices support it, review Windows Bluetooth LE " +
                          "Audio / Shared Audio options. VB-CABLE cannot bypass an " +
                          "endpoint that Windows refuses to create.");
            }
            else
            {
                AppendLog("The selected outputs were not classified as Bluetooth. " +
                          "Review their audio drivers and try a different wired/USB " +
                          "combination. A virtual cable cannot bypass an endpoint " +
                          "that Windows refuses to create.");
            }
        }
    }

    private static string GetCompatibilityDisplayName(
        CompatibilityClassification classification) => classification switch
    {
        CompatibilityClassification.Compatible => "Passed",
        CompatibilityClassification.IndividualDeviceFailure =>
            "Individual output failed",
        CompatibilityClassification.SecondEndpointResourceLimitLikely =>
            "Likely system limitation",
        CompatibilityClassification.DeviceSpecificSimultaneousFailure =>
            "Device-specific simultaneous failure",
        CompatibilityClassification.OrderSensitive => "Opening order sensitive",
        CompatibilityClassification.FormatConversionFailure =>
            "Format conversion failed",
        _ => "Unknown failure"
    };

    private int InsertInitialSilence(
        BufferedWaveProvider buffer,
        WaveFormat waveFormat,
        int delayMilliseconds,
        string outputDeviceName,
        string routeLabel)
    {
        var delay = AudioDelayHelper.ApplyInitialSilence(
            waveFormat,
            delayMilliseconds,
            buffer.AddSamples);

        AppendLog($"{routeLabel} device: {outputDeviceName}");
        AppendLog($"{routeLabel} DelayMs: {delayMilliseconds}");
        AppendLog($"{routeLabel} WaveFormat: {waveFormat}");
        AppendLog($"{routeLabel} AverageBytesPerSecond: {waveFormat.AverageBytesPerSecond}");
        AppendLog($"{routeLabel} BlockAlign: {waveFormat.BlockAlign}");
        AppendLog($"{routeLabel} calculated silenceBytes: {delay.CalculatedBytes}");
        AppendLog($"{routeLabel} final aligned silenceBytes: {delay.AlignedBytes}");
        AppendLog($"{routeLabel} applied delay: {delay.AppliedDelayMilliseconds:F2} ms");

        if (delay.AlignedBytes > 0)
        {
            AppendLog($"{routeLabel}: silence was written to this output buffer.");
        }
        else
        {
            AppendLog($"{routeLabel} warning: inserted delay is zero; no silence was written.");
        }

        if (buffer.BufferedBytes < delay.AlignedBytes)
        {
            AppendLog($"{routeLabel} warning: delay buffer was not applied as expected.");
        }

        return delay.AlignedBytes;
    }

    private WasapiOut CreateAndInitializeOutput(
        MMDevice device,
        IWaveProvider provider,
        string label,
        int latencyMilliseconds)
    {
        AppendLog($"Opening {label}: {device.FriendlyName}");

        WasapiOut? output = null;

        try
        {
            // Polling mode is more compatible with some Bluetooth audio drivers.
            output = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                false,
                latencyMilliseconds);

            output.Init(provider);
            AppendLog($"{label} opened successfully.");
            return output;
        }
        catch (Exception exception)
        {
            if (output is not null)
            {
                try
                {
                    output.Dispose();
                }
                catch (Exception disposeException)
                {
                    AppendLog($"{label} cleanup after endpoint open failure also " +
                              $"failed: {disposeException.Message}");
                }
            }

            throw new InvalidOperationException(
                $"{label} could not open {device.FriendlyName} ({device.ID}): " +
                $"{exception.Message}",
                exception);
        }
    }

    private void StartFileTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFileTestDevices(out var outputDevice1, out var outputDevice2))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select an audio file",
            Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3|WAV files (*.wav)|*.wav|MP3 files (*.mp3)|*.mp3",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            AppendLog("File test cancelled.");
            return;
        }

        StopRouting(logMessage: IsRoutingActive());
        StopFileTest(logMessage: false);

        try
        {
            var reader1 = new AudioFileReader(dialog.FileName);
            _fileReader1 = reader1;

            var reader2 = new AudioFileReader(dialog.FileName);
            _fileReader2 = reader2;

            var providerChain1 = CreateFileTestProviderChain(
                reader1,
                outputDevice1,
                "Output Device 1");
            _fileProviderChain1 = providerChain1;

            var providerChain2 = CreateFileTestProviderChain(
                reader2,
                outputDevice2,
                "Output Device 2");
            _fileProviderChain2 = providerChain2;

            var output1 = CreateAndInitializeOutput(
                outputDevice1,
                providerChain1.OutputProvider,
                "File Test Output Device 1",
                300);
            _fileOutput1 = output1;

            var output2 = CreateAndInitializeOutput(
                outputDevice2,
                providerChain2.OutputProvider,
                "File Test Output Device 2",
                300);
            _fileOutput2 = output2;

            output1.PlaybackStopped += FileOutput_PlaybackStopped;
            output2.PlaybackStopped += FileOutput_PlaybackStopped;

            output1.Play();
            output2.Play();

            UpdateControlState();
            AppendLog($"File test started: {dialog.FileName}");
            AppendLog($"Output Device 1: {outputDevice1.FriendlyName} ({outputDevice1.ID})");
            AppendLog($"Output Device 2: {outputDevice2.FriendlyName} ({outputDevice2.ID})");
        }
        catch (Exception exception)
        {
            var error = AudioErrorClassifier.Classify(exception);
            AppendLog($"Could not start file test: {error.KnownSymbolicName} " +
                      $"{error.HResultHex} - {error.UserFacingDescription}");
            StopFileTest(logMessage: false);
        }
    }

    private OutputProviderChain CreateFileTestProviderChain(
        AudioFileReader reader,
        MMDevice outputDevice,
        string routeLabel)
    {
        WaveFormat targetMixFormat;

        try
        {
            targetMixFormat = AudioFormatAdapter.GetDeviceMixFormat(outputDevice);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"File Test {routeLabel} could not read the Windows mix format " +
                $"for {outputDevice.FriendlyName} ({outputDevice.ID}): " +
                exception.Message,
                exception);
        }

        AppendLog($"Route: File Test {routeLabel}");
        AppendLog($"Source file format: {reader.WaveFormat}");
        AppendLog($"Device mix format: {targetMixFormat}");

        try
        {
            var chain = OutputProviderChain.Create(reader, targetMixFormat);
            AppendLog($"Conversion mode: {chain.ConversionMode}");

            if (chain.UsesConversion)
            {
                AppendLog($"Media Foundation resampler quality: " +
                          OutputProviderChain.MediaFoundationResamplerQuality);
            }

            return chain;
        }
        catch (AudioFormatConversionException exception)
        {
            throw new InvalidOperationException(
                $"File Test {routeLabel} format conversion setup failed for " +
                $"{outputDevice.FriendlyName}: {exception.Message}",
                exception);
        }
    }

    private void StopFileTestButton_Click(object sender, RoutedEventArgs e)
    {
        StopFileTest(logMessage: true);
    }

    private async void CalibrationClickButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (IsCalibrationTestActive())
        {
            StopCalibrationTest(logMessage: true);
            return;
        }

        if (!TryGetDiagnosticDevices(out var outputDevice1, out var outputDevice2) ||
            !TryGetRoutingDelays(out var output1DelayMs, out var output2DelayMs))
        {
            return;
        }

        if (IsRoutingActive() || IsFileTestActive() || _isRunningHardwareDiagnostic)
        {
            AppendLog("Calibration Click Test cannot start while another audio test is active.");
            return;
        }

        try
        {
            var format1 = GetDeviceMixFormat(outputDevice1);
            var format2 = GetDeviceMixFormat(outputDevice2);
            var buffer1 = CreateCalibrationBuffer(format1, output1DelayMs);
            var buffer2 = CreateCalibrationBuffer(format2, output2DelayMs);
            _calibrationBuffer1 = buffer1;
            _calibrationBuffer2 = buffer2;

            InsertInitialSilence(
                buffer1,
                format1,
                output1DelayMs,
                outputDevice1.FriendlyName,
                "Calibration Output 1");
            InsertInitialSilence(
                buffer2,
                format2,
                output2DelayMs,
                outputDevice2.FriendlyName,
                "Calibration Output 2");

            var clickBlock1 = CreateOneSecondClickBlock(format1);
            var clickBlock2 = CreateOneSecondClickBlock(format2);

            for (var i = 0; i < 4; i++)
            {
                buffer1.AddSamples(clickBlock1, 0, clickBlock1.Length);
                buffer2.AddSamples(clickBlock2, 0, clickBlock2.Length);
            }

            AppendLog("Calibration buffers preloaded with one click per second.");

            var output1 = CreateAndInitializeOutput(
                outputDevice1,
                buffer1,
                "Calibration Output Device 1",
                300);
            _calibrationOutput1 = output1;

            var output2 = CreateAndInitializeOutput(
                outputDevice2,
                buffer2,
                "Calibration Output Device 2",
                300);
            _calibrationOutput2 = output2;

            output1.PlaybackStopped += CalibrationOutput_PlaybackStopped;
            output2.PlaybackStopped += CalibrationOutput_PlaybackStopped;

            _calibrationCancellation = new CancellationTokenSource();
            output1.Play();
            output2.Play();

            UpdateControlState();
            AppendLog("Calibration Click Test started. A short beep will play every second.");
            AppendLog("Set one output to 1000 ms and the other to 0 ms to hear an obvious one-second difference.");

            await RunCalibrationProducerAsync(
                buffer1,
                clickBlock1,
                buffer2,
                clickBlock2,
                _calibrationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal stop path.
        }
        catch (Exception exception)
        {
            AppendLog($"Calibration Click Test failed: {exception.Message}");
            AppendLog($"Exception details: {exception}");
        }
        finally
        {
            StopCalibrationTest(logMessage: false);
        }
    }

    private static BufferedWaveProvider CreateCalibrationBuffer(
        WaveFormat waveFormat,
        int delayMilliseconds)
    {
        return new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(delayMilliseconds + 5000),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }

    private static WaveFormat GetDeviceMixFormat(MMDevice device)
    {
        using var audioClient = device.AudioClient;
        return audioClient.MixFormat;
    }

    private static async Task RunCalibrationProducerAsync(
        BufferedWaveProvider buffer1,
        byte[] clickBlock1,
        BufferedWaveProvider buffer2,
        byte[] clickBlock2,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(1000, cancellationToken);
            buffer1.AddSamples(clickBlock1, 0, clickBlock1.Length);
            buffer2.AddSamples(clickBlock2, 0, clickBlock2.Length);
        }
    }

    private static byte[] CreateOneSecondClickBlock(WaveFormat waveFormat)
    {
        var block = AudioDelayHelper.CreateSilenceBytes(
            waveFormat,
            waveFormat.AverageBytesPerSecond);
        var clickFrames = Math.Max(1, waveFormat.SampleRate / 20);
        var isFloat = AudioDelayHelper.IsFloatFormat(waveFormat);

        for (var frame = 0; frame < clickFrames; frame++)
        {
            var envelope = 1.0 - (double)frame / clickFrames;
            var sample = Math.Sin(2 * Math.PI * 1000 * frame / waveFormat.SampleRate) *
                         0.55 *
                         envelope;

            for (var channel = 0; channel < waveFormat.Channels; channel++)
            {
                var sampleOffset =
                    frame * waveFormat.BlockAlign +
                    channel * (waveFormat.BitsPerSample / 8);
                WriteSample(block, sampleOffset, waveFormat, isFloat, sample);
            }
        }

        return block;
    }

    private static void WriteSample(
        byte[] buffer,
        int offset,
        WaveFormat waveFormat,
        bool isFloat,
        double sample)
    {
        if (isFloat && waveFormat.BitsPerSample == 32)
        {
            BitConverter.GetBytes((float)sample).CopyTo(buffer, offset);
            return;
        }

        switch (waveFormat.BitsPerSample)
        {
            case 8:
                buffer[offset] = (byte)Math.Clamp(128 + sample * 127, 0, 255);
                break;
            case 16:
                BitConverter.GetBytes((short)(sample * short.MaxValue))
                    .CopyTo(buffer, offset);
                break;
            case 24:
                var value24 = (int)(sample * 8388607);
                buffer[offset] = (byte)value24;
                buffer[offset + 1] = (byte)(value24 >> 8);
                buffer[offset + 2] = (byte)(value24 >> 16);
                break;
            case 32:
                BitConverter.GetBytes((int)(sample * int.MaxValue))
                    .CopyTo(buffer, offset);
                break;
            default:
                throw new NotSupportedException(
                    $"Calibration click generation does not support {waveFormat.BitsPerSample}-bit audio.");
        }
    }

    private void CalibrationOutput_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isStoppingCalibrationTest)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (e.Exception is not null)
            {
                AppendLog($"Calibration output failed: {e.Exception.Message}");
            }
            else
            {
                AppendLog("A calibration output stopped unexpectedly.");
            }

            StopCalibrationTest(logMessage: false);
        });
    }

    private bool IsCalibrationTestActive()
    {
        return _calibrationCancellation is not null ||
               _calibrationOutput1 is not null ||
               _calibrationOutput2 is not null;
    }

    private void StopCalibrationTest(bool logMessage)
    {
        if (_isStoppingCalibrationTest)
        {
            return;
        }

        _isStoppingCalibrationTest = true;

        var cancellation = _calibrationCancellation;
        var output1 = _calibrationOutput1;
        var output2 = _calibrationOutput2;
        var wasActive = IsCalibrationTestActive();

        _calibrationCancellation = null;
        _calibrationBuffer1 = null;
        _calibrationBuffer2 = null;
        _calibrationOutput1 = null;
        _calibrationOutput2 = null;

        try
        {
            cancellation?.Cancel();
            cancellation?.Dispose();

            if (output1 is not null)
            {
                output1.PlaybackStopped -= CalibrationOutput_PlaybackStopped;
                TryStopAndDisposeOutput(output1, "Calibration Output Device 1");
            }

            if (output2 is not null)
            {
                output2.PlaybackStopped -= CalibrationOutput_PlaybackStopped;
                TryStopAndDisposeOutput(output2, "Calibration Output Device 2");
            }

            if (logMessage && wasActive)
            {
                AppendLog("Calibration Click Test stopped.");
            }
        }
        finally
        {
            _isStoppingCalibrationTest = false;
            UpdateControlState();
        }
    }

    private void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized && IsRoutingActive())
        {
            AppendLog("Delay changes require stopping and restarting routing.");
        }
    }

    private async void HardwareDiagnosticButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetDiagnosticDevices(out var outputDevice1, out var outputDevice2))
        {
            return;
        }

        if (IsRoutingActive() || IsFileTestActive() || IsCalibrationTestActive())
        {
            AppendLog("Hardware diagnostic cannot run while routing, file testing, or calibration is active.");
            return;
        }

        _isRunningHardwareDiagnostic = true;
        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        UpdateControlState();

        try
        {
            _activeDiagnosticTask = _hardwareDiagnosticService.RunAsync(
                SourceDeviceComboBox.SelectedItem as MMDevice,
                outputDevice1,
                outputDevice2,
                _diagnosticCancellation.Token);
            var report = await _activeDiagnosticTask;
            _lastDiagnosticReport = report.TechnicalReport;
            CopyDiagnosticReportButton.IsEnabled = true;
            CompatibilityAssessmentTextBlock.Text =
                $"Full Diagnostic: " +
                $"{GetCompatibilityDisplayName(report.Classification)}" +
                $"{Environment.NewLine}" +
                report.LikelyClassification;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Hardware diagnostic was cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog($"Hardware diagnostic could not complete: {exception.Message}");
            AppendLog($"Exception details: {exception}");
        }
        finally
        {
            _isRunningHardwareDiagnostic = false;
            _activeDiagnosticTask = null;
            _diagnosticCancellation?.Dispose();
            _diagnosticCancellation = null;
            UpdateControlState();
        }
    }

    private void CopyDiagnosticReportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastDiagnosticReport))
        {
            AppendLog("No completed diagnostic report is available to copy.");
            return;
        }

        try
        {
            Clipboard.SetText(_lastDiagnosticReport);
            AppendLog("Diagnostic report copied to the clipboard.");
        }
        catch (Exception exception)
        {
            AppendLog($"Could not copy diagnostic report: {exception.Message}");
        }
    }

    private void FileOutput_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isStoppingFileTest || !IsFileTestActive())
            {
                return;
            }

            if (e.Exception is not null)
            {
                AppendLog($"A file-test output or provider failed after startup: " +
                          e.Exception.Message);
                StopFileTest(logMessage: false);
                return;
            }

            AppendLog("File test playback completed.");
            StopFileTest(logMessage: false);
        });
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingDevices || sender is not ComboBox comboBox)
        {
            return;
        }

        var label = comboBox.Tag as string ?? "Device";

        try
        {
            CompatibilityAssessmentTextBlock.Text =
                "Compatibility: Selection changed; a new preflight is required.";

            if (comboBox.SelectedItem is MMDevice device)
            {
                AppendLog($"{label} selected: {device.FriendlyName}");
                AppendLog($"{label} ID: {device.ID}");
            }
            else
            {
                AppendLog($"{label} selection cleared.");
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Could not read the selected {label}: {exception.Message}");
        }
    }

    private void RefreshDevices()
    {
        if (_deviceEnumerator is null)
        {
            AppendLog("Audio device enumerator is not available.");
            return;
        }

        StopFileTest(logMessage: IsFileTestActive());
        StopRouting(logMessage: IsRoutingActive());
        StopCalibrationTest(logMessage: IsCalibrationTestActive());
        _deviceRefreshVersion++;
        _compatibilityService.InvalidateCache();
        CompatibilityAssessmentTextBlock.Text =
            "Compatibility: Not tested after device refresh.";

        var selectedSourceId = GetSelectedDeviceId(SourceDeviceComboBox.SelectedItem);
        var selectedOutput1Id = GetSelectedDeviceId(OutputDevice1ComboBox.SelectedItem);
        var selectedOutput2Id = GetSelectedDeviceId(OutputDevice2ComboBox.SelectedItem);

        _isRefreshingDevices = true;

        try
        {
            DisposeDevices();

            var deviceCollection = _deviceEnumerator.EnumerateAudioEndPoints(
                DataFlow.Render,
                DeviceState.Active);

            _devices = deviceCollection.ToList();

            SourceDeviceComboBox.ItemsSource = _devices;
            OutputDevice1ComboBox.ItemsSource = _devices;
            OutputDevice2ComboBox.ItemsSource = _devices;

            RestoreSelection(SourceDeviceComboBox, selectedSourceId);
            RestoreSelection(OutputDevice1ComboBox, selectedOutput1Id);
            RestoreSelection(OutputDevice2ComboBox, selectedOutput2Id);
            SelectInitialDevices();

            AppendLog($"Found {_devices.Count} active audio output device(s).");
            LogCurrentSelections();
        }
        catch (Exception exception)
        {
            AppendLog($"Could not enumerate audio devices: {exception.Message}");
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    private void SelectInitialDevices()
    {
        if (_devices.Count == 0)
        {
            return;
        }

        if (SourceDeviceComboBox.SelectedItem is null)
        {
            MMDevice? defaultDevice = null;

            try
            {
                defaultDevice = _deviceEnumerator!.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

                SourceDeviceComboBox.SelectedItem =
                    _devices.FirstOrDefault(device => device.ID == defaultDevice.ID);
            }
            catch
            {
                // A default device may not exist even when inactive endpoints are present.
            }
            finally
            {
                defaultDevice?.Dispose();
            }

            SourceDeviceComboBox.SelectedItem ??= _devices[0];
        }

        var sourceId = GetSelectedDeviceId(SourceDeviceComboBox.SelectedItem);
        var outputCandidates = _devices
            .Where(device => device.ID != sourceId)
            .ToList();

        OutputDevice1ComboBox.SelectedItem ??= outputCandidates.ElementAtOrDefault(0);
        OutputDevice2ComboBox.SelectedItem ??= outputCandidates.ElementAtOrDefault(1);
    }

    private void LogCurrentSelections()
    {
        LogSelectedDevice("Source Device", SourceDeviceComboBox.SelectedItem);
        LogSelectedDevice("Output Device 1", OutputDevice1ComboBox.SelectedItem);
        LogSelectedDevice("Output Device 2", OutputDevice2ComboBox.SelectedItem);
    }

    private void LogSourceSetup(MMDevice sourceDevice)
    {
        var name = sourceDevice.FriendlyName;
        var looksVirtual =
            name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        AppendLog(looksVirtual
            ? "Recommended virtual cable source detected."
            : "Physical source selected. A virtual audio cable is recommended for stable, feedback-safe routing.");
    }

    private bool TryGetRoutingDevices(
        out MMDevice sourceDevice,
        out MMDevice outputDevice1,
        out MMDevice outputDevice2)
    {
        sourceDevice = null!;
        outputDevice1 = null!;
        outputDevice2 = null!;

        if (SourceDeviceComboBox.SelectedItem is not MMDevice selectedSource)
        {
            AppendLog("Routing was not started because no Source Device is selected.");
            AppendLog("Select VB-CABLE or another virtual audio cable as Source Device, then try again.");
            return false;
        }

        if (OutputDevice1ComboBox.SelectedItem is not MMDevice selectedOutput1)
        {
            AppendLog("Routing was not started because Output Device 1 is not selected.");
            AppendLog("Select the first headphone or speaker, then try again.");
            return false;
        }

        if (OutputDevice2ComboBox.SelectedItem is not MMDevice selectedOutput2)
        {
            AppendLog("Routing was not started because Output Device 2 is not selected.");
            AppendLog("Select the second headphone or speaker, then try again.");
            return false;
        }

        if (selectedOutput1.ID == selectedOutput2.ID)
        {
            AppendLog("Routing was not started because both outputs are set to the same device.");
            AppendLog("Choose two different headphones or speakers.");
            return false;
        }

        if (selectedSource.ID == selectedOutput1.ID)
        {
            AppendLog("Routing was not started because Source Device and Output Device 1 are the same.");
            AppendLog("This can cause feedback or recursive audio. Use a virtual cable as the source and a separate playback device as Output Device 1.");
            return false;
        }

        if (selectedSource.ID == selectedOutput2.ID)
        {
            AppendLog("Routing was not started because Source Device and Output Device 2 are the same.");
            AppendLog("This can cause feedback or recursive audio. Use a virtual cable as the source and a separate playback device as Output Device 2.");
            return false;
        }

        sourceDevice = selectedSource;
        outputDevice1 = selectedOutput1;
        outputDevice2 = selectedOutput2;
        return true;
    }

    private bool TryGetRoutingDelays(
        out int output1DelayMilliseconds,
        out int output2DelayMilliseconds)
    {
        output1DelayMilliseconds =
            (int)Math.Round(Output1DelaySlider.Value / 10.0) * 10;
        output2DelayMilliseconds =
            (int)Math.Round(Output2DelaySlider.Value / 10.0) * 10;
        return true;
    }

    private bool TryGetFileTestDevices(
        out MMDevice outputDevice1,
        out MMDevice outputDevice2)
    {
        outputDevice1 = null!;
        outputDevice2 = null!;

        if (OutputDevice1ComboBox.SelectedItem is not MMDevice selectedOutput1 ||
            OutputDevice2ComboBox.SelectedItem is not MMDevice selectedOutput2)
        {
            AppendLog("Select Output Device 1 and Output Device 2 before starting the file test.");
            return false;
        }

        if (selectedOutput1.ID == selectedOutput2.ID)
        {
            AppendLog("Output Device 1 and Output Device 2 must be different.");
            return false;
        }

        outputDevice1 = selectedOutput1;
        outputDevice2 = selectedOutput2;
        return true;
    }

    private bool TryGetDiagnosticDevices(
        out MMDevice outputDevice1,
        out MMDevice outputDevice2)
    {
        outputDevice1 = null!;
        outputDevice2 = null!;

        if (OutputDevice1ComboBox.SelectedItem is not MMDevice selectedOutput1)
        {
            AppendLog("Hardware diagnostic cannot start: Select Output Device 1.");
            return false;
        }

        if (OutputDevice2ComboBox.SelectedItem is not MMDevice selectedOutput2)
        {
            AppendLog("Hardware diagnostic cannot start: Select Output Device 2.");
            return false;
        }

        if (selectedOutput1.ID == selectedOutput2.ID)
        {
            AppendLog("Hardware diagnostic cannot start: Select two different output devices.");
            return false;
        }

        outputDevice1 = selectedOutput1;
        outputDevice2 = selectedOutput2;
        return true;
    }

    private bool IsFileTestActive()
    {
        return _fileOutput1 is not null ||
               _fileOutput2 is not null ||
               _fileProviderChain1 is not null ||
               _fileProviderChain2 is not null ||
               _fileReader1 is not null ||
               _fileReader2 is not null;
    }

    private bool IsRoutingActive()
    {
        return _routingEngine.IsActive;
    }

    private void StopRouting(bool logMessage)
    {
        if (!IsRoutingActive())
        {
            if (logMessage)
            {
                AppendLog("Routing is not active.");
            }

            return;
        }

        _routingEngine.Stop(logMessage);

        UpdateControlState();
    }

    private void StopFileTest(bool logMessage)
    {
        if (_isStoppingFileTest)
        {
            return;
        }

        _isStoppingFileTest = true;

        var output1 = _fileOutput1;
        var output2 = _fileOutput2;
        var providerChain1 = _fileProviderChain1;
        var providerChain2 = _fileProviderChain2;
        var reader1 = _fileReader1;
        var reader2 = _fileReader2;

        _fileOutput1 = null;
        _fileOutput2 = null;
        _fileProviderChain1 = null;
        _fileProviderChain2 = null;
        _fileReader1 = null;
        _fileReader2 = null;

        try
        {
            if (output1 is not null)
            {
                output1.PlaybackStopped -= FileOutput_PlaybackStopped;
                TryStopAndDisposeOutput(output1, "Output Device 1");
            }

            if (output2 is not null)
            {
                output2.PlaybackStopped -= FileOutput_PlaybackStopped;
                TryStopAndDisposeOutput(output2, "Output Device 2");
            }

            TryDisposeProviderChain(providerChain1, "File Test Output Device 1");
            TryDisposeProviderChain(providerChain2, "File Test Output Device 2");

            TryDisposeReader(reader1, "Audio reader 1");
            TryDisposeReader(reader2, "Audio reader 2");

            if (logMessage &&
                (output1 is not null ||
                 output2 is not null ||
                 providerChain1 is not null ||
                 providerChain2 is not null))
            {
                AppendLog("File test stopped.");
            }
        }
        finally
        {
            _isStoppingFileTest = false;
            UpdateControlState();
        }
    }

    private void UpdateControlState()
    {
        var routingActive = IsRoutingActive();
        var fileTestActive = IsFileTestActive();
        var calibrationActive = IsCalibrationTestActive();
        var audioActive =
            routingActive ||
            fileTestActive ||
            calibrationActive ||
            _isRunningHardwareDiagnostic ||
            _isRunningCompatibilityPreflight;

        SourceDeviceComboBox.IsEnabled = !audioActive;
        OutputDevice1ComboBox.IsEnabled = !audioActive;
        OutputDevice2ComboBox.IsEnabled = !audioActive;
        Output1DelaySlider.IsEnabled = !audioActive;
        Output2DelaySlider.IsEnabled = !audioActive;
        RefreshDevicesButton.IsEnabled = !audioActive;
        EnableFastPreflightCheckBox.IsEnabled = !audioActive;

        StartRoutingButton.IsEnabled = !audioActive;
        StopRoutingButton.IsEnabled =
            routingActive || _isRunningCompatibilityPreflight;
        StartFileTestButton.IsEnabled = !audioActive;
        StopFileTestButton.IsEnabled = fileTestActive;
        HardwareDiagnosticButton.IsEnabled = !audioActive;
        CalibrationClickButton.IsEnabled =
            calibrationActive || !audioActive;
        CalibrationClickButton.Content = calibrationActive
            ? "Stop Calibration Click Test"
            : "Start Calibration Click Test";
        CopyDiagnosticReportButton.IsEnabled =
            !string.IsNullOrWhiteSpace(_lastDiagnosticReport);
    }

    private void TryStopAndDisposeOutput(WasapiOut output, string label)
    {
        try
        {
            output.Stop();
        }
        catch (Exception exception)
        {
            AppendLog($"{label} could not be stopped cleanly: {exception.Message}");
        }

        try
        {
            output.Dispose();
        }
        catch (Exception exception)
        {
            AppendLog($"{label} could not be disposed cleanly: {exception.Message}");
        }
    }

    private void TryDisposeReader(AudioFileReader? reader, string label)
    {
        if (reader is null)
        {
            return;
        }

        try
        {
            reader.Dispose();
        }
        catch (Exception exception)
        {
            AppendLog($"{label} could not be disposed cleanly: {exception.Message}");
        }
    }

    private void TryDisposeProviderChain(
        OutputProviderChain? providerChain,
        string label)
    {
        if (providerChain is null)
        {
            return;
        }

        try
        {
            providerChain.Dispose();
        }
        catch (Exception exception)
        {
            AppendLog($"{label} conversion chain could not be disposed cleanly: " +
                      exception.Message);
        }
    }

    private void LogSelectedDevice(string label, object? selectedItem)
    {
        try
        {
            if (selectedItem is MMDevice device)
            {
                AppendLog($"{label}: {device.FriendlyName}");
                AppendLog($"{label} ID: {device.ID}");
                return;
            }

            AppendLog($"{label}: not selected.");
        }
        catch (Exception exception)
        {
            AppendLog($"Could not read {label}: {exception.Message}");
        }
    }

    private static string? GetSelectedDeviceId(object? selectedItem)
    {
        return (selectedItem as MMDevice)?.ID;
    }

    private void RestoreSelection(ComboBox comboBox, string? deviceId)
    {
        if (deviceId is null)
        {
            return;
        }

        comboBox.SelectedItem = _devices.FirstOrDefault(device => device.ID == deviceId);
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private void RoutingEngine_LogMessage(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendLog(message);
            return;
        }

        Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    private void RoutingEngine_StateChanged(RoutingState state)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyRoutingStateChange(state);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyRoutingStateChange(state));
    }

    private void ApplyRoutingStateChange(RoutingState state)
    {
        if (state == RoutingState.Starting)
        {
            _lastRouteWarningCounts.Clear();
        }

        UpdateControlState();

        if (state is RoutingState.Stopped or RoutingState.Faulted)
        {
            UpdateRoutingTelemetry();
        }
    }

    private void RoutingTelemetryTimer_Tick(object? sender, EventArgs e)
    {
        UpdateRoutingTelemetry();
    }

    private void UpdateRoutingTelemetry()
    {
        IReadOnlyList<OutputRouteSnapshot> snapshots;

        try
        {
            snapshots = _routingEngine.GetRouteSnapshots();
        }
        catch (Exception exception)
        {
            RouteStatusTextBlock.Text =
                $"Routing telemetry unavailable: {exception.Message}";
            return;
        }

        if (snapshots.Count == 0)
        {
            RouteStatusTextBlock.Text =
                $"Routing state: {_routingEngine.State}.";
            return;
        }

        RouteStatusTextBlock.Text = string.Join(
            Environment.NewLine,
            snapshots.Select(snapshot =>
                $"{snapshot.Label} | Buffer: " +
                $"{snapshot.BufferedMilliseconds:F0} ms / " +
                $"{snapshot.BufferCapacityMilliseconds:F0} ms | " +
                $"Underflow: {snapshot.UnderflowCount} | " +
                $"Overflow: {snapshot.OverflowCount} | " +
                $"State: {snapshot.PlaybackState}"));

        var activeRouteIds = snapshots
            .Select(snapshot => snapshot.RouteId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleRouteId in _lastRouteWarningCounts.Keys
                     .Where(routeId => !activeRouteIds.Contains(routeId))
                     .ToArray())
        {
            _lastRouteWarningCounts.Remove(staleRouteId);
        }

        foreach (var snapshot in snapshots)
        {
            _lastRouteWarningCounts.TryGetValue(
                snapshot.RouteId,
                out var previous);

            if (snapshot.UnderflowCount > previous.Underflows)
            {
                AppendLog(
                    $"{snapshot.Label} buffer underflow warning: " +
                    $"+{snapshot.UnderflowCount - previous.Underflows}, " +
                    $"total {snapshot.UnderflowCount}.");
            }

            if (snapshot.OverflowCount > previous.Overflows)
            {
                AppendLog(
                    $"{snapshot.Label} buffer overflow warning: " +
                    $"+{snapshot.OverflowCount - previous.Overflows}, " +
                    $"total {snapshot.OverflowCount}; estimated dropped bytes: " +
                    snapshot.EstimatedDroppedBytes);
            }

            _lastRouteWarningCounts[snapshot.RouteId] =
                (snapshot.UnderflowCount, snapshot.OverflowCount);
        }
    }

    private void HardwareDiagnosticService_LogMessage(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendLog(message);
            return;
        }

        Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    private void CompatibilityService_LogMessage(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendLog(message);
            return;
        }

        Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    private void DisposeDevices()
    {
        SourceDeviceComboBox.ItemsSource = null;
        OutputDevice1ComboBox.ItemsSource = null;
        OutputDevice2ComboBox.ItemsSource = null;

        foreach (var device in _devices)
        {
            device.Dispose();
        }

        _devices.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        _compatibilityCancellation?.Cancel();
        _diagnosticCancellation?.Cancel();
        _calibrationCancellation?.Cancel();
        WaitForBackgroundCleanup(_activeCompatibilityTask);
        WaitForBackgroundCleanup(_activeDiagnosticTask);
        _routingTelemetryTimer.Stop();
        _routingTelemetryTimer.Tick -= RoutingTelemetryTimer_Tick;
        _routingEngine.Dispose();
        _routingEngine.LogMessage -= RoutingEngine_LogMessage;
        _routingEngine.StateChanged -= RoutingEngine_StateChanged;
        _hardwareDiagnosticService.LogMessage -= HardwareDiagnosticService_LogMessage;
        _compatibilityService.LogMessage -= CompatibilityService_LogMessage;
        _hardwareDiagnosticService.Dispose();
        _compatibilityService.Dispose();
        StopFileTest(logMessage: false);
        StopCalibrationTest(logMessage: false);
        DisposeDevices();
        _deviceEnumerator?.Dispose();
        base.OnClosed(e);
    }

    private static void WaitForBackgroundCleanup(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException exception)
            when (exception.InnerExceptions.All(inner =>
                inner is OperationCanceledException ||
                inner is TaskCanceledException))
        {
            // Expected after window-close cancellation; service finally blocks
            // have already released their temporary WASAPI resources.
        }
        catch (AggregateException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Background audio cleanup completed with an error: {exception}");
        }
    }
}
