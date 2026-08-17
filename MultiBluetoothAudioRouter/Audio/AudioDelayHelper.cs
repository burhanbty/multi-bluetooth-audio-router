using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public static class AudioDelayHelper
{
    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public static DelayApplicationResult ApplyInitialSilence(
        WaveFormat waveFormat,
        int requestedDelayMilliseconds,
        Action<byte[], int, int> writeSilence)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        ArgumentNullException.ThrowIfNull(writeSilence);

        var result = Calculate(waveFormat, requestedDelayMilliseconds);

        if (result.AlignedBytes > 0)
        {
            var silence = CreateSilenceBytes(waveFormat, result.AlignedBytes);
            writeSilence(silence, 0, silence.Length);
        }

        return result;
    }

    public static DelayApplicationResult Calculate(
        WaveFormat waveFormat,
        int requestedDelayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        ArgumentOutOfRangeException.ThrowIfNegative(requestedDelayMilliseconds);

        var calculatedBytesLong =
            (long)requestedDelayMilliseconds *
            waveFormat.AverageBytesPerSecond /
            1000;

        if (calculatedBytesLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedDelayMilliseconds),
                "The requested delay is too large for one audio buffer.");
        }

        var calculatedBytes = (int)calculatedBytesLong;
        var alignedBytes = calculatedBytes /
                           waveFormat.BlockAlign *
                           waveFormat.BlockAlign;
        var appliedDelayMilliseconds = waveFormat.AverageBytesPerSecond == 0
            ? 0
            : alignedBytes * 1000.0 / waveFormat.AverageBytesPerSecond;

        return new DelayApplicationResult(
            requestedDelayMilliseconds,
            calculatedBytes,
            alignedBytes,
            appliedDelayMilliseconds);
    }

    public static byte[] CreateSilenceBytes(
        WaveFormat waveFormat,
        int byteCount)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        var silence = new byte[byteCount];

        if (!IsFloatFormat(waveFormat) && waveFormat.BitsPerSample == 8)
        {
            Array.Fill(silence, (byte)128);
        }

        return silence;
    }

    public static bool IsFloatFormat(WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        return waveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
               waveFormat is WaveFormatExtensible extensible &&
               extensible.SubFormat == IeeeFloatSubFormat;
    }
}

public sealed record DelayApplicationResult(
    int RequestedDelayMilliseconds,
    int CalculatedBytes,
    int AlignedBytes,
    double AppliedDelayMilliseconds);
