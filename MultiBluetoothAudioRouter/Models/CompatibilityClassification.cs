namespace MultiBluetoothAudioRouter.Models;

public enum CompatibilityClassification
{
    Compatible,
    IndividualDeviceFailure,
    SecondEndpointResourceLimitLikely,
    DeviceSpecificSimultaneousFailure,
    OrderSensitive,
    FormatConversionFailure,
    UnknownFailure
}
