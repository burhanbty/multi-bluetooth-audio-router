namespace MultiBluetoothAudioRouter.Models;

public enum DiagnosticStepStatus
{
    Success,
    Failed,
    Skipped
}

public enum DiagnosticOperation
{
    ReadMixFormat,
    CreateWasapiOut,
    Initialize,
    Play,
    HoldOpen,
    Stop,
    Dispose
}

public enum DiagnosticEndpointPosition
{
    OnlyEndpoint,
    First,
    Second,
    Subsequent,
    Both
}

public sealed record DiagnosticExceptionDetail(
    string ExceptionType,
    string Message,
    int HResult,
    string HResultHex);

public sealed record DiagnosticStepResult(
    string ScenarioId,
    string ScenarioLabel,
    string DeviceLabel,
    string DeviceName,
    string DeviceId,
    DiagnosticEndpointPosition Position,
    DiagnosticOperation Operation,
    DiagnosticStepStatus Status,
    string Message,
    IReadOnlyList<DiagnosticExceptionDetail> Exceptions,
    AudioErrorInfo? ErrorInfo = null)
{
    public bool Succeeded => Status == DiagnosticStepStatus.Success;

    public bool Failed => Status == DiagnosticStepStatus.Failed;
}
