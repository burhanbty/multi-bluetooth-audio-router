namespace MultiBluetoothAudioRouter.Models;

public sealed record OutputRouteSnapshot(
    string RouteId,
    string Label,
    string DeviceName,
    int ManualDelayMilliseconds,
    double AppliedDelayMilliseconds,
    long InitialSilenceBytes,
    string SourceFormat,
    string TargetMixFormat,
    string ConversionMode,
    double BufferedMilliseconds,
    double BufferCapacityMilliseconds,
    long TotalWrittenBytes,
    long TotalReadBytes,
    long UnderflowCount,
    long OverflowCount,
    long EstimatedDroppedBytes,
    string PlaybackState);
