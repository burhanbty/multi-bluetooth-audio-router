using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class OutputProviderChain : IDisposable
{
    public const int MediaFoundationResamplerQuality = 60;

    private readonly MediaFoundationResampler? _resampler;
    private int _disposeStarted;

    private OutputProviderChain(
        IWaveProvider sourceProvider,
        WaveFormat targetFormat,
        MediaFoundationResampler? resampler)
    {
        SourceFormat = sourceProvider.WaveFormat;
        TargetFormat = targetFormat;
        _resampler = resampler;
        OutputProvider = resampler is null
            ? sourceProvider
            : new ConversionErrorWaveProvider(
                resampler,
                SourceFormat,
                TargetFormat);
        ConversionMode = resampler is null
            ? "Direct"
            : "MediaFoundationResampler";
    }

    public IWaveProvider OutputProvider { get; }

    public WaveFormat SourceFormat { get; }

    public WaveFormat TargetFormat { get; }

    public string ConversionMode { get; }

    public bool UsesConversion => _resampler is not null;

    public static OutputProviderChain Create(
        IWaveProvider sourceProvider,
        WaveFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(targetFormat);

        if (AudioFormatAdapter.AreCompatible(
                sourceProvider.WaveFormat,
                targetFormat))
        {
            return new OutputProviderChain(sourceProvider, targetFormat, null);
        }

        MediaFoundationResampler? resampler = null;

        try
        {
            resampler = new MediaFoundationResampler(
                sourceProvider,
                targetFormat);
            resampler.ResamplerQuality = MediaFoundationResamplerQuality;

            return new OutputProviderChain(
                sourceProvider,
                targetFormat,
                resampler);
        }
        catch (Exception exception)
        {
            Exception failure = exception;

            try
            {
                resampler?.Dispose();
            }
            catch (Exception disposeException)
            {
                failure = new AggregateException(
                    "Format conversion setup and resampler cleanup both failed.",
                    exception,
                    disposeException);
            }

            throw new AudioFormatConversionException(
                sourceProvider.WaveFormat,
                targetFormat,
                failure);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _resampler?.Dispose();
    }

    private sealed class ConversionErrorWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _inner;
        private readonly WaveFormat _sourceFormat;
        private readonly WaveFormat _targetFormat;

        public ConversionErrorWaveProvider(
            IWaveProvider inner,
            WaveFormat sourceFormat,
            WaveFormat targetFormat)
        {
            _inner = inner;
            _sourceFormat = sourceFormat;
            _targetFormat = targetFormat;
        }

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Audio format conversion failed while reading from " +
                    $"'{_sourceFormat}' to '{_targetFormat}': " +
                    exception.Message,
                    exception);
            }
        }
    }
}

public sealed class AudioFormatConversionException : Exception
{
    public AudioFormatConversionException(
        WaveFormat sourceFormat,
        WaveFormat targetFormat,
        Exception innerException)
        : base(
            $"Could not create an audio conversion chain from " +
            $"'{sourceFormat}' to '{targetFormat}': {innerException.Message}",
            innerException)
    {
        SourceFormat = sourceFormat;
        TargetFormat = targetFormat;
    }

    public WaveFormat SourceFormat { get; }

    public WaveFormat TargetFormat { get; }
}
