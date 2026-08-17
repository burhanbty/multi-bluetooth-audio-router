namespace MultiBluetoothAudioRouter.Models;

public enum AudioFailureCategory
{
    EndpointCreateFailed,
    UnsupportedFormat,
    DeviceInvalidated,
    DeviceInUse,
    ExclusiveModeConflict,
    AudioServiceUnavailable,
    AccessDenied,
    OperationCancelled,
    Unknown
}
