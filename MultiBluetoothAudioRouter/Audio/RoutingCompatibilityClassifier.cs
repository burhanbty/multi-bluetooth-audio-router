using MultiBluetoothAudioRouter.Models;

namespace MultiBluetoothAudioRouter.Audio;

public static class RoutingCompatibilityClassifier
{
    public static CompatibilityClassification Classify(
        IReadOnlyList<OutputOpenAttemptResult> individualAttempts,
        OutputOrderAttemptResult forward,
        OutputOrderAttemptResult reverse)
    {
        if (individualAttempts.Any(attempt => attempt.IsFormatConversionFailure) ||
            forward.Outputs.Any(attempt => attempt.IsFormatConversionFailure) ||
            reverse.Outputs.Any(attempt => attempt.IsFormatConversionFailure))
        {
            return CompatibilityClassification.FormatConversionFailure;
        }

        if (individualAttempts.Any(attempt =>
                !attempt.Succeeded &&
                attempt.FailureCategory != AudioFailureCategory.OperationCancelled))
        {
            return CompatibilityClassification.IndividualDeviceFailure;
        }

        if (forward.Succeeded && reverse.Succeeded)
        {
            return CompatibilityClassification.Compatible;
        }

        if (forward.Succeeded != reverse.Succeeded)
        {
            return CompatibilityClassification.OrderSensitive;
        }

        if (!forward.Succeeded && !reverse.Succeeded)
        {
            var sameFailedDevice =
                !string.IsNullOrWhiteSpace(forward.FailedDeviceId) &&
                string.Equals(
                    forward.FailedDeviceId,
                    reverse.FailedDeviceId,
                    StringComparison.OrdinalIgnoreCase);

            if (sameFailedDevice)
            {
                return CompatibilityClassification.DeviceSpecificSimultaneousFailure;
            }

            var sameSubsequentPosition =
                forward.FailedOpeningOrderIndex is >= 1 &&
                reverse.FailedOpeningOrderIndex == forward.FailedOpeningOrderIndex;
            var endpointCreateFailures =
                forward.FailureCategory == AudioFailureCategory.EndpointCreateFailed &&
                reverse.FailureCategory == AudioFailureCategory.EndpointCreateFailed;

            if (sameSubsequentPosition && endpointCreateFailures)
            {
                return CompatibilityClassification.SecondEndpointResourceLimitLikely;
            }
        }

        return CompatibilityClassification.UnknownFailure;
    }
}
