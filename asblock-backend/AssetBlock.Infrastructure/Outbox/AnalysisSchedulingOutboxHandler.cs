using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Outbox;

/// <summary>
/// Recovers durable analysis scheduling intents. The enqueue itself rereads the verified immutable
/// evidence, the current version inputs and the exact pinned policy under the
/// Asset/workspace/version locks inside one transaction, so a recovered intent can only produce a
/// complete coherent run/job pair. Proof is never reconstructed: a missing or superseded proof, a
/// quarantined or deleted version, or a deliberately disabled analysis mode schedules nothing,
/// while a recoverable gap (no pinned policy yet, an older run still executing) retries.
/// </summary>
internal sealed class AnalysisSchedulingOutboxHandler(
    ApplicationDbContext dbContext,
    IAnalysisRunEnqueuer enqueuer,
    IOptions<CodeAnalysisOptions> analysisOptions,
    ILogger<AnalysisSchedulingOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ANALYSIS_SCHEDULING;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        AnalysisSchedulingPayload payload = JsonSerializer.Deserialize<AnalysisSchedulingPayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid AnalysisScheduling payload.");

        if (!analysisOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Analysis scheduling is deliberately disabled; the intent stays queued.");
        }

        DateTimeOffset dbNow = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>(/*-sql*/ """SELECT clock_timestamp() AS "Value" """)
            .FirstAsync(cancellationToken);

        // The enqueue validates the full current proof under its own locks and owns the
        // transaction, so this handler can never leave a run without its bound job.
        AnalysisRunEnqueueResult enqueued = await enqueuer.Enqueue(
            payload.AssetId,
            payload.AssetVersionId,
            payload.SecurityRecheckId,
            payload.ArchiveEvidenceId,
            payload.MalwareEvidenceId,
            payload.SecurityGeneration,
            payload.ContentSha256,
            dbNow,
            cancellationToken);

        switch (enqueued.Rejection)
        {
            case AnalysisEnqueueRejection.NONE:
                logger.LogInformation(
                    "Recovered analysis scheduling for version {VersionId} as run {RunId}.",
                    payload.AssetVersionId, enqueued.AnalysisRunId);
                return;

            case AnalysisEnqueueRejection.NOT_CURRENT:
            case AnalysisEnqueueRejection.PROOF_SUPERSEDED:
                // The advertised asset, bytes or security selection moved on: retrying can never
                // make this intent current again, so it is dropped with an explicit reason.
                logger.LogWarning(
                    "Analysis scheduling intent for version {VersionId} is no longer current ({Rejection}); scheduling nothing.",
                    payload.AssetVersionId, enqueued.Rejection);
                return;

            default:
                // A missing pinned policy or an older run that is still executing resolves on a
                // later attempt; anything else stays queued until the bounded attempts record an
                // explicit dead letter carrying the exact reason.
                throw new InvalidOperationException(
                    $"Recovered analysis scheduling for version {payload.AssetVersionId} was not admitted ({enqueued.Rejection}).");
        }
    }
}
