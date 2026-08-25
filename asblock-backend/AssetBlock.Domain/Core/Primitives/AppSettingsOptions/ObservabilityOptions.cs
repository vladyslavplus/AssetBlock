namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed record ObservabilityOptions
{
    public const string SECTION_NAME = "Observability";

    public bool Enabled { get; init; }
    public string ServiceName { get; init; } = "AssetBlock.WebApi";
    public string OtlpEndpoint { get; init; } = "http://127.0.0.1:4317";
    public bool ExportTraces { get; init; } = true;
    public bool ExportMetrics { get; init; } = true;
    public bool ExportLogs { get; init; } = true;
    public double TraceSampleRatio { get; init; } = 1.0;
}
