namespace MultiBluetoothAudioRouter.Models;

public sealed record DiagnosticScenarioResult(
    string ScenarioId,
    string DisplayName,
    bool Succeeded,
    IReadOnlyList<DiagnosticStepResult> Steps)
{
    public DiagnosticStepResult? Failure => Steps.FirstOrDefault(step => step.Failed);
}

public sealed record HardwareDiagnosticReport(
    DiagnosticScenarioResult Output1Alone,
    DiagnosticScenarioResult Output2Alone,
    DiagnosticScenarioResult Order1Then2,
    DiagnosticScenarioResult Order2Then1,
    string LikelyClassification,
    CompatibilityClassification Classification,
    bool IsConclusionProbabilistic,
    IReadOnlyList<AudioDeviceDescriptor> OutputDevices,
    string TechnicalReport)
{
    public IReadOnlyList<DiagnosticScenarioResult> Scenarios =>
        [Output1Alone, Output2Alone, Order1Then2, Order2Then1];

    public IReadOnlyList<DiagnosticStepResult> Steps =>
        Scenarios.SelectMany(scenario => scenario.Steps).ToArray();
}
