using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public static class AudioFormatAdapter
{
    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");

    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public static WaveFormat GetDeviceMixFormat(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        using var audioClient = device.AudioClient;
        return audioClient.MixFormat;
    }

    public static bool AreCompatible(WaveFormat source, WaveFormat target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return source.SampleRate == target.SampleRate &&
               source.Channels == target.Channels &&
               source.BitsPerSample == target.BitsPerSample &&
               source.BlockAlign == target.BlockAlign &&
               source.AverageBytesPerSecond == target.AverageBytesPerSecond &&
               NormalizeEncoding(source) == NormalizeEncoding(target);
    }

    private static WaveFormatEncoding NormalizeEncoding(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible)
        {
            return format.Encoding;
        }

        if (extensible.SubFormat == PcmSubFormat)
        {
            return WaveFormatEncoding.Pcm;
        }

        if (extensible.SubFormat == IeeeFloatSubFormat)
        {
            return WaveFormatEncoding.IeeeFloat;
        }

        return format.Encoding;
    }
}
