using System.Diagnostics.CodeAnalysis;
using MultiBluetoothAudioRouter.Models;
using NAudio.CoreAudioApi;

namespace MultiBluetoothAudioRouter.Audio;

public sealed class AudioDeviceInspector
{
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The inspector is an injected service boundary for endpoint metadata.")]
    public AudioDeviceDescriptor Inspect(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var interfaceName = GetStringProperty(
            device,
            PropertyKeys.PKEY_DeviceInterface_FriendlyName);
        var description = GetStringProperty(
            device,
            PropertyKeys.PKEY_Device_DeviceDesc);
        var controllerId = GetStringProperty(
            device,
            PropertyKeys.PKEY_Device_ControllerDeviceId);
        var instanceId = GetStringProperty(
            device,
            PropertyKeys.PKEY_Device_InstanceId);
        var metadata = string.Join(
            " | ",
            new[]
            {
                device.ID,
                interfaceName,
                description,
                controllerId,
                instanceId
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var classification = ClassifyTransport(device.FriendlyName, metadata);

        string mixFormat;
        try
        {
            mixFormat = AudioFormatAdapter.GetDeviceMixFormat(device).ToString();
        }
        catch (Exception exception)
        {
            mixFormat = $"Unavailable: {exception.Message}";
        }

        return new AudioDeviceDescriptor(
            device.ID,
            device.FriendlyName,
            device.State.ToString(),
            interfaceName,
            description,
            controllerId,
            instanceId,
            classification.Kind,
            classification.IsHeuristic,
            classification.Evidence,
            mixFormat);
    }

    private static (AudioDeviceTransportKind Kind, bool IsHeuristic, string Evidence)
        ClassifyTransport(string friendlyName, string metadata)
    {
        if (ContainsAny(metadata, "BTHLEDEVICE", "Bluetooth LE", "LE Audio"))
        {
            return (
                AudioDeviceTransportKind.BluetoothLeAudioCandidate,
                false,
                "Bluetooth LE-related identifier found in endpoint metadata; " +
                "treated as a candidate, not proof of active LE Audio transport.");
        }

        if (ContainsAny(metadata, "BTHENUM", "BTHHFENUM", "Bluetooth"))
        {
            return (
                AudioDeviceTransportKind.BluetoothClassicOrUnknownBluetooth,
                false,
                "Bluetooth identifier found in endpoint metadata.");
        }

        if (ContainsAny(metadata, "USB\\", "USB Audio"))
        {
            return (
                AudioDeviceTransportKind.UsbAudio,
                false,
                "USB identifier found in endpoint metadata.");
        }

        if (ContainsAny(metadata, "DISPLAY\\", "HDMI", "Display Audio"))
        {
            return (
                AudioDeviceTransportKind.HdmiOrDisplayAudio,
                false,
                "Display/HDMI identifier found in endpoint metadata.");
        }

        if (ContainsAny(metadata, "HDAUDIO\\"))
        {
            return (
                AudioDeviceTransportKind.BuiltInAudio,
                false,
                "HD Audio identifier found in endpoint metadata.");
        }

        if (ContainsAny(friendlyName, "CABLE", "Virtual", "Voicemeeter", "VAC"))
        {
            return (
                AudioDeviceTransportKind.VirtualAudio,
                true,
                "Inferred from the endpoint friendly name.");
        }

        if (ContainsAny(friendlyName, "Bluetooth", "AirPods", "FreeBuds", "QCY"))
        {
            return (
                AudioDeviceTransportKind.BluetoothClassicOrUnknownBluetooth,
                true,
                "Bluetooth transport inferred from endpoint naming only.");
        }

        if (ContainsAny(friendlyName, "Realtek", "Built-in", "Internal"))
        {
            return (
                AudioDeviceTransportKind.BuiltInAudio,
                true,
                "Built-in audio inferred from endpoint naming only.");
        }

        if (ContainsAny(friendlyName, "Headphone", "Headset", "Speaker", "Line Out"))
        {
            return (
                AudioDeviceTransportKind.WiredAnalog,
                true,
                "Wired analog transport inferred from endpoint naming only.");
        }

        return (
            AudioDeviceTransportKind.Unknown,
            true,
            "No reliable transport property was available.");
    }

    private static string GetStringProperty(MMDevice device, PropertyKey key)
    {
        try
        {
            return device.Properties.TryGetValue<string>(key, out var value)
                ? value ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
