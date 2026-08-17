using MultiBluetoothAudioRouter.Audio;
using MultiBluetoothAudioRouter.Models;

namespace MultiBluetoothAudioRouter.Tests;

public static class RoutingCompatibilityClassifierTests
{
    public static IReadOnlyList<TestCase> Cases =>
    [
        new(nameof(Classify_WhenBothOrdersSucceed_ReturnsCompatible), Classify_WhenBothOrdersSucceed_ReturnsCompatible),
        new(nameof(Classify_WhenOneDeviceFailsAlone_ReturnsIndividualDeviceFailure), Classify_WhenOneDeviceFailsAlone_ReturnsIndividualDeviceFailure),
        new(nameof(Classify_WhenLaterEndpointFailsInBothOrders_ReturnsResourceLimitLikely), Classify_WhenLaterEndpointFailsInBothOrders_ReturnsResourceLimitLikely),
        new(nameof(Classify_WhenSameDeviceFailsInBothOrders_ReturnsDeviceSpecificFailure), Classify_WhenSameDeviceFailsInBothOrders_ReturnsDeviceSpecificFailure),
        new(nameof(Classify_WhenOnlyOneOrderSucceeds_ReturnsOrderSensitive), Classify_WhenOnlyOneOrderSucceeds_ReturnsOrderSensitive),
        new(nameof(Classify_WhenConversionFails_ReturnsFormatConversionFailure), Classify_WhenConversionFails_ReturnsFormatConversionFailure),
        new(nameof(Classify_WhenThirdEndpointFails_ReturnsResourceLimitLikely), Classify_WhenThirdEndpointFails_ReturnsResourceLimitLikely)
    ];

    private static void Classify_WhenBothOrdersSucceed_ReturnsCompatible()
    {
        var devices = Individuals("A", "B");

        var result = RoutingCompatibilityClassifier.Classify(
            devices,
            Order("forward", ["A", "B"]),
            Order("reverse", ["B", "A"]));

        TestAssert.Equal(CompatibilityClassification.Compatible, result);
    }

    private static void Classify_WhenOneDeviceFailsAlone_ReturnsIndividualDeviceFailure()
    {
        var devices = new[]
        {
            Attempt("A", false, AudioFailureCategory.EndpointCreateFailed),
            Attempt("B", true)
        };

        var result = RoutingCompatibilityClassifier.Classify(
            devices,
            Order("forward", ["A", "B"], 0, "A"),
            Order("reverse", ["B", "A"], 1, "A"));

        TestAssert.Equal(CompatibilityClassification.IndividualDeviceFailure, result);
    }

    private static void Classify_WhenLaterEndpointFailsInBothOrders_ReturnsResourceLimitLikely()
    {
        var result = RoutingCompatibilityClassifier.Classify(
            Individuals("A", "B"),
            Order("forward", ["A", "B"], 1, "B"),
            Order("reverse", ["B", "A"], 1, "A"));

        TestAssert.Equal(
            CompatibilityClassification.SecondEndpointResourceLimitLikely,
            result);
    }

    private static void Classify_WhenSameDeviceFailsInBothOrders_ReturnsDeviceSpecificFailure()
    {
        var result = RoutingCompatibilityClassifier.Classify(
            Individuals("A", "B"),
            Order("forward", ["A", "B"], 1, "B"),
            Order("reverse", ["B", "A"], 0, "B"));

        TestAssert.Equal(
            CompatibilityClassification.DeviceSpecificSimultaneousFailure,
            result);
    }

    private static void Classify_WhenOnlyOneOrderSucceeds_ReturnsOrderSensitive()
    {
        var result = RoutingCompatibilityClassifier.Classify(
            Individuals("A", "B"),
            Order("forward", ["A", "B"], 1, "B"),
            Order("reverse", ["B", "A"]));

        TestAssert.Equal(CompatibilityClassification.OrderSensitive, result);
    }

    private static void Classify_WhenConversionFails_ReturnsFormatConversionFailure()
    {
        var devices = new[]
        {
            Attempt(
                "A",
                false,
                AudioFailureCategory.UnsupportedFormat,
                isFormatConversionFailure: true),
            Attempt("B", true)
        };

        var result = RoutingCompatibilityClassifier.Classify(
            devices,
            Order("forward", ["A", "B"]),
            Order("reverse", ["B", "A"]));

        TestAssert.Equal(CompatibilityClassification.FormatConversionFailure, result);
    }

    private static void Classify_WhenThirdEndpointFails_ReturnsResourceLimitLikely()
    {
        var result = RoutingCompatibilityClassifier.Classify(
            Individuals("A", "B", "C"),
            Order("forward", ["A", "B", "C"], 2, "C"),
            Order("reverse", ["C", "B", "A"], 2, "A"));

        TestAssert.Equal(
            CompatibilityClassification.SecondEndpointResourceLimitLikely,
            result);
    }

    private static OutputOpenAttemptResult[] Individuals(
        params string[] deviceIds) =>
        deviceIds.Select(deviceId => Attempt(deviceId, true)).ToArray();

    private static OutputOpenAttemptResult Attempt(
        string deviceId,
        bool succeeded,
        AudioFailureCategory? failureCategory = null,
        bool isFormatConversionFailure = false) => new(
        deviceId,
        deviceId,
        deviceId,
        0,
        succeeded,
        succeeded ? null : DiagnosticOperation.Initialize,
        failureCategory,
        null,
        isFormatConversionFailure ? "Failed" : "Direct",
        isFormatConversionFailure);

    private static OutputOrderAttemptResult Order(
        string name,
        IReadOnlyList<string> deviceIds,
        int? failedIndex = null,
        string failedDeviceId = "")
    {
        var succeeded = failedIndex is null;
        var outputs = deviceIds.Select((deviceId, index) =>
            Attempt(
                deviceId,
                succeeded || index < failedIndex,
                index == failedIndex
                    ? AudioFailureCategory.EndpointCreateFailed
                    : null)).ToArray();

        return new OutputOrderAttemptResult(
            name,
            deviceIds,
            succeeded,
            failedIndex,
            failedDeviceId,
            succeeded ? null : DiagnosticOperation.Initialize,
            succeeded ? null : AudioFailureCategory.EndpointCreateFailed,
            null,
            outputs);
    }
}
