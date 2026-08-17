namespace MultiBluetoothAudioRouter.Models;

public sealed record AudioExceptionChainEntry(
    string ExceptionType,
    string Message,
    int HResultSigned,
    string HResultHex);

public sealed record AudioErrorInfo(
    string ExceptionType,
    string OriginalMessage,
    int HResultSigned,
    string HResultHex,
    string KnownSymbolicName,
    AudioFailureCategory FailureCategory,
    string UserFacingDescription,
    string TechnicalDescription,
    IReadOnlyList<AudioExceptionChainEntry> InnerExceptionChain);
