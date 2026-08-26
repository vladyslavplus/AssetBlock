using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Observability;
using System.Diagnostics;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class AiTelemetry : IAiTelemetry
{
    public const string UNKNOWN_MODEL = "UNKNOWN";
    private const string ACTIVITY_NAME = "assetblock.ai.generate";
    public const string REQUEST_ID_TAG = "ai.request_id";

    public IDisposable? StartActivity() =>
        AssetBlockDiagnostics.ActivitySource.StartActivity(ACTIVITY_NAME);

    public void Record(
        AiProviderKind? provider,
        string? allowlistedModelId,
        AiTelemetryOutcome outcome,
        TimeSpan duration,
        int? inputTokens,
        int? outputTokens,
        string? requestId)
    {
        Activity.Current?.SetTag(
            REQUEST_ID_TAG,
            string.IsNullOrWhiteSpace(requestId) ? null : Truncate(requestId));

        AssetBlockDiagnostics.RecordAiGeneration(
            provider,
            string.IsNullOrWhiteSpace(allowlistedModelId) ? UNKNOWN_MODEL : allowlistedModelId,
            ToDiagnosticsOutcome(outcome),
            duration,
            inputTokens,
            outputTokens);
    }

    private static AiDiagnosticsOutcome ToDiagnosticsOutcome(AiTelemetryOutcome outcome) => outcome switch
    {
        AiTelemetryOutcome.SUCCESS => AiDiagnosticsOutcome.SUCCESS,
        AiTelemetryOutcome.DISABLED => AiDiagnosticsOutcome.DISABLED,
        AiTelemetryOutcome.RETRYABLE => AiDiagnosticsOutcome.RETRYABLE,
        AiTelemetryOutcome.CANCELLED => AiDiagnosticsOutcome.CANCELLED,
        _ => AiDiagnosticsOutcome.TERMINAL
    };

    private static string Truncate(string value) =>
        value.Length <= 128 ? value : value[..128];
}
