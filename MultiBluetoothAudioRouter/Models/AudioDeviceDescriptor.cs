namespace MultiBluetoothAudioRouter.Models;

public sealed record AudioDeviceDescriptor(
    string DeviceId,
    string FriendlyName,
    string DeviceState,
    string InterfaceFriendlyName,
    string DeviceDescription,
    string ControllerDeviceId,
    string InstanceId,
    AudioDeviceTransportKind TransportKind,
    bool IsTransportClassificationHeuristic,
    string TransportEvidence,
    string MixFormat);
