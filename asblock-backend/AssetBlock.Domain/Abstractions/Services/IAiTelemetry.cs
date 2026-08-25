using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAiTelemetry
{
    IDisposable? StartActivity();

    void Record(
        AiProviderKind? provider,
        string? allowlistedModelId,
        AiTelemetryOutcome outcome,
        TimeSpan duration,
        int? inputTokens,
        int? outputTokens,
        string? requestId);
}
