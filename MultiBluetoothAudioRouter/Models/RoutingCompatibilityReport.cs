namespace MultiBluetoothAudioRouter.Models;

public sealed record RoutingCompatibilityReport(
    CompatibilityClassification Classification,
    string Summary,
    bool IsConclusionProbabilistic,
    bool WasServedFromCache,
    IReadOnlyList<AudioDeviceDescriptor> OutputDevices,
    IReadOnlyList<OutputOpenAttemptResult> IndividualAttempts,
    OutputOrderAttemptResult ForwardOrder,
    OutputOrderAttemptResult ReverseOrder,
    string CacheKey)
{
    public bool IsCompatible => Classification == CompatibilityClassification.Compatible;

    public RoutingCompatibilityReport AsCached() => this with
    {
        WasServedFromCache = true
    };
}
