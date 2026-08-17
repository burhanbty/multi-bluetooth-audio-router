using MultiBluetoothAudioRouter.Audio;
using MultiBluetoothAudioRouter.Models;

namespace MultiBluetoothAudioRouter.Tests;

public static class AudioErrorClassifierTests
{
    public static IReadOnlyList<TestCase> Cases =>
    [
        new(nameof(Classify_EndpointCreateFailure_ReturnsKnownWasapiError), Classify_EndpointCreateFailure_ReturnsKnownWasapiError),
        new(nameof(Classify_Cancellation_ReturnsCancelledError), Classify_Cancellation_ReturnsCancelledError),
        new(nameof(Classify_UnknownHResult_PreservesUnknownCategory), Classify_UnknownHResult_PreservesUnknownCategory)
    ];

    private static void Classify_EndpointCreateFailure_ReturnsKnownWasapiError()
    {
        var exception = new TestHResultException(
            "endpoint create failed",
            AudioErrorClassifier.AudclntEndpointCreateFailed);

        var result = AudioErrorClassifier.Classify(exception);

        TestAssert.Equal(AudioFailureCategory.EndpointCreateFailed, result.FailureCategory);
        TestAssert.Equal("AUDCLNT_E_ENDPOINT_CREATE_FAILED", result.KnownSymbolicName);
    }

    private static void Classify_Cancellation_ReturnsCancelledError()
    {
        var result = AudioErrorClassifier.Classify(
            new OperationCanceledException("cancelled"));

        TestAssert.Equal(AudioFailureCategory.OperationCancelled, result.FailureCategory);
        TestAssert.Equal("ERROR_CANCELLED", result.KnownSymbolicName);
    }

    private static void Classify_UnknownHResult_PreservesUnknownCategory()
    {
        var result = AudioErrorClassifier.Classify(
            new TestHResultException("unknown", unchecked((int)0x81234567)));

        TestAssert.Equal(AudioFailureCategory.Unknown, result.FailureCategory);
        TestAssert.Equal("UNKNOWN_AUDIO_ERROR", result.KnownSymbolicName);
        TestAssert.Equal("0x81234567", result.HResultHex);
    }

    private sealed class TestHResultException : Exception
    {
        public TestHResultException(string message, int hResult)
            : base(message)
        {
            HResult = hResult;
        }
    }
}
