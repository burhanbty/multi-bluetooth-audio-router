using MultiBluetoothAudioRouter.Models;

namespace MultiBluetoothAudioRouter.Audio;

public static class AudioErrorClassifier
{
    public const int AudclntEndpointCreateFailed = unchecked((int)0x8889000F);
    public const int AudclntUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int AudclntServiceNotRunning = unchecked((int)0x88890010);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int OperationCancelled = unchecked((int)0x800704C7);

    public static AudioErrorInfo Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chain = EnumerateExceptionChain(exception).ToArray();
        var selected = chain.FirstOrDefault(item =>
            GetCategory(item.HResult) != AudioFailureCategory.Unknown);
        selected ??= chain[0];

        var category = exception is OperationCanceledException
            ? AudioFailureCategory.OperationCancelled
            : GetCategory(selected.HResult);
        var symbolicName = GetSymbolicName(category);
        var descriptions = GetDescriptions(category);

        return new AudioErrorInfo(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            selected.HResult,
            $"0x{unchecked((uint)selected.HResult):X8}",
            symbolicName,
            category,
            descriptions.UserFacing,
            descriptions.Technical,
            chain.Select(item => new AudioExceptionChainEntry(
                    item.GetType().FullName ?? item.GetType().Name,
                    item.Message,
                    item.HResult,
                    $"0x{unchecked((uint)item.HResult):X8}"))
                .ToArray());
    }

    public static AudioFailureCategory GetCategory(int hResult)
    {
        return hResult switch
        {
            AudclntEndpointCreateFailed => AudioFailureCategory.EndpointCreateFailed,
            AudclntUnsupportedFormat => AudioFailureCategory.UnsupportedFormat,
            AudclntDeviceInvalidated => AudioFailureCategory.DeviceInvalidated,
            AudclntDeviceInUse => AudioFailureCategory.DeviceInUse,
            AudclntExclusiveModeNotAllowed => AudioFailureCategory.ExclusiveModeConflict,
            AudclntServiceNotRunning => AudioFailureCategory.AudioServiceUnavailable,
            AccessDenied => AudioFailureCategory.AccessDenied,
            OperationCancelled => AudioFailureCategory.OperationCancelled,
            _ => AudioFailureCategory.Unknown
        };
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(
        Exception exception)
    {
        for (var current = exception;
             current is not null;
             current = current.InnerException)
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    foreach (var nested in EnumerateExceptionChain(inner))
                    {
                        yield return nested;
                    }
                }

                yield break;
            }
        }
    }

    private static string GetSymbolicName(AudioFailureCategory category)
    {
        return category switch
        {
            AudioFailureCategory.EndpointCreateFailed =>
                "AUDCLNT_E_ENDPOINT_CREATE_FAILED",
            AudioFailureCategory.UnsupportedFormat =>
                "AUDCLNT_E_UNSUPPORTED_FORMAT",
            AudioFailureCategory.DeviceInvalidated =>
                "AUDCLNT_E_DEVICE_INVALIDATED",
            AudioFailureCategory.DeviceInUse => "AUDCLNT_E_DEVICE_IN_USE",
            AudioFailureCategory.ExclusiveModeConflict =>
                "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED",
            AudioFailureCategory.AudioServiceUnavailable =>
                "AUDCLNT_E_SERVICE_NOT_RUNNING",
            AudioFailureCategory.AccessDenied => "E_ACCESSDENIED",
            AudioFailureCategory.OperationCancelled => "ERROR_CANCELLED",
            _ => "UNKNOWN_AUDIO_ERROR"
        };
    }

    private static (string UserFacing, string Technical) GetDescriptions(
        AudioFailureCategory category)
    {
        return category switch
        {
            AudioFailureCategory.EndpointCreateFailed => (
                "Windows could not create the selected audio endpoint. The " +
                "device may be unavailable, or the current audio/Bluetooth " +
                "driver stack may not have enough resources to open it together " +
                "with the other selected outputs.",
                "WASAPI shared-mode endpoint creation failed. This is distinct " +
                "from AUDCLNT_E_UNSUPPORTED_FORMAT and is not, by itself, proof " +
                "of a Bluetooth hardware limitation."),
            AudioFailureCategory.UnsupportedFormat => (
                "The requested audio format is not supported by this endpoint.",
                "WASAPI returned AUDCLNT_E_UNSUPPORTED_FORMAT."),
            AudioFailureCategory.DeviceInvalidated => (
                "The audio device was disconnected, disabled, or changed.",
                "The active WASAPI endpoint became invalid."),
            AudioFailureCategory.DeviceInUse => (
                "The audio device is currently unavailable because another " +
                "audio client is using it incompatibly.",
                "WASAPI reported that the device is in use."),
            AudioFailureCategory.ExclusiveModeConflict => (
                "The endpoint cannot be opened with the current exclusive-mode " +
                "policy.",
                "Exclusive mode is not permitted for this endpoint."),
            AudioFailureCategory.AudioServiceUnavailable => (
                "The Windows Audio service is not available.",
                "The WASAPI audio service is not running."),
            AudioFailureCategory.AccessDenied => (
                "Windows denied access to the selected audio endpoint.",
                "The operation failed with E_ACCESSDENIED."),
            AudioFailureCategory.OperationCancelled => (
                "The audio operation was cancelled.",
                "Cancellation was requested before the operation completed."),
            _ => (
                "An unknown audio error occurred.",
                "The HRESULT is not mapped to a known audio failure category.")
        };
    }
}
