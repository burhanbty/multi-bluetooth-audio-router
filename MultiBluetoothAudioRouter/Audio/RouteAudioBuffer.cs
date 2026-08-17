using NAudio.Wave;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class RouteAudioBuffer : IWaveProvider
{
    private readonly BufferedWaveProvider _buffer;
    private long _totalWrittenBytes;
    private long _totalReadBytes;
    private long _initialSilenceBytes;
    private long _overflowCount;
    private long _estimatedDroppedBytes;
    private long _underflowCount;

    public RouteAudioBuffer(
        WaveFormat waveFormat,
        TimeSpan bufferDuration)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            bufferDuration,
            TimeSpan.Zero);

        _buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = bufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }

    public WaveFormat WaveFormat => _buffer.WaveFormat;

    public int CapacityBytes => _buffer.BufferLength;

    public double CapacityMilliseconds => BytesToMilliseconds(CapacityBytes);

    public int BufferedBytes => _buffer.BufferedBytes;

    public double BufferedMilliseconds => BytesToMilliseconds(BufferedBytes);

    public long TotalWrittenBytes => Interlocked.Read(ref _totalWrittenBytes);

    public long TotalReadBytes => Interlocked.Read(ref _totalReadBytes);

    public long InitialSilenceBytes => Interlocked.Read(ref _initialSilenceBytes);

    public long CapturedAudioBytes => Math.Max(
        0,
        TotalWrittenBytes - InitialSilenceBytes);

    public double CapturedAudioMilliseconds =>
        BytesToMilliseconds(CapturedAudioBytes);

    public long OverflowCount => Interlocked.Read(ref _overflowCount);

    public long EstimatedDroppedBytes =>
        Interlocked.Read(ref _estimatedDroppedBytes);

    public long UnderflowCount => Interlocked.Read(ref _underflowCount);

    public void Write(byte[] source, int offset, int count)
    {
        WriteCore(source, offset, count, isInitialSilence: false);
    }

    public void WriteInitialSilence(byte[] source, int offset, int count)
    {
        WriteCore(source, offset, count, isInitialSilence: true);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        // BufferedWaveProvider is internally thread-safe. Reading its current
        // count before Read gives low-cost telemetry without making the capture
        // callback wait on an additional route-level lock.
        var availableBeforeRead = _buffer.BufferedBytes;
        if (availableBeforeRead < count)
        {
            Interlocked.Increment(ref _underflowCount);
        }

        var bytesRead = _buffer.Read(buffer, offset, count);
        Interlocked.Add(ref _totalReadBytes, bytesRead);
        return bytesRead;
    }

    public RouteAudioBufferMetrics GetMetrics()
    {
        return new RouteAudioBufferMetrics(
            BufferedBytes,
            BufferedMilliseconds,
            CapacityBytes,
            CapacityMilliseconds,
            TotalWrittenBytes,
            TotalReadBytes,
            InitialSilenceBytes,
            CapturedAudioBytes,
            UnderflowCount,
            OverflowCount,
            EstimatedDroppedBytes);
    }

    private void WriteCore(
        byte[] source,
        int offset,
        int count,
        bool isInitialSilence)
    {
        ValidateBufferArguments(source, offset, count);

        // This is intentionally an estimate: the output thread may consume data
        // between this read and AddSamples. It avoids blocking the capture thread.
        var availableSpace = Math.Max(0, CapacityBytes - _buffer.BufferedBytes);
        var estimatedDropped = Math.Max(0, count - availableSpace);
        var estimatedWritten = count - estimatedDropped;

        if (estimatedDropped > 0)
        {
            Interlocked.Increment(ref _overflowCount);
            Interlocked.Add(ref _estimatedDroppedBytes, estimatedDropped);
        }

        _buffer.AddSamples(source, offset, count);
        Interlocked.Add(ref _totalWrittenBytes, estimatedWritten);

        if (isInitialSilence)
        {
            Interlocked.Add(ref _initialSilenceBytes, estimatedWritten);
        }
    }

    private double BytesToMilliseconds(long bytes)
    {
        return WaveFormat.AverageBytesPerSecond == 0
            ? 0
            : bytes * 1000.0 / WaveFormat.AverageBytesPerSecond;
    }

    private static void ValidateBufferArguments(
        byte[] buffer,
        int offset,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Offset and count must identify a valid buffer range.");
        }
    }
}

public sealed record RouteAudioBufferMetrics(
    int BufferedBytes,
    double BufferedMilliseconds,
    int CapacityBytes,
    double CapacityMilliseconds,
    long TotalWrittenBytes,
    long TotalReadBytes,
    long InitialSilenceBytes,
    long CapturedAudioBytes,
    long UnderflowCount,
    long OverflowCount,
    long EstimatedDroppedBytes);
