namespace MultiBluetoothAudioRouter.Models;

public sealed record OutputOpenAttemptResult(
    string DeviceId,
    string DeviceName,
    string Label,
    int OpeningOrderIndex,
    bool Succeeded,
    DiagnosticOperation? FailureOperation,
    AudioFailureCategory? FailureCategory,
    AudioErrorInfo? ErrorInfo,
    string ConversionMode,
    bool IsFormatConversionFailure = false);

public sealed record OutputOrderAttemptResult(
    string Name,
    IReadOnlyList<string> OrderedDeviceIds,
    bool Succeeded,
    int? FailedOpeningOrderIndex,
    string FailedDeviceId,
    DiagnosticOperation? FailureOperation,
    AudioFailureCategory? FailureCategory,
    AudioErrorInfo? ErrorInfo,
    IReadOnlyList<OutputOpenAttemptResult> Outputs);
