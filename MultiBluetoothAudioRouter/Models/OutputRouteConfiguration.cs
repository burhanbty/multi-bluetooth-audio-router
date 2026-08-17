using NAudio.CoreAudioApi;

namespace MultiBluetoothAudioRouter.Models;

public sealed record OutputRouteConfiguration(
    string RouteId,
    string Label,
    MMDevice OutputDevice,
    int DelayMilliseconds,
    int WasapiLatencyMilliseconds = 300);
