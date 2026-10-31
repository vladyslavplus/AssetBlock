using System.Data;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Leased finalizer for confirmed-terminated analysis responses. One commit owns report,
/// stages, findings, server admission, selection, job and run completion plus audit and outbox.
/// Same frozen response replay returns the persisted report without duplicate effects;
/// an altered same-run response conflicts. Tools are never rerun for a replay.
/// </summary>
internal sealed class CodeAnalysisReportFinalizer(
    ApplicationDbContext dbContext,
    ICodeAnalysisReportValidator validator,
    ILogger<CodeAnalysisReportFinalizer> logger) : ICodeAnalysisReportFinalizer
{
    public async Task<AssetProcessingJobOutcome> FinalizeConfirmed(
        Guid jobId,
        Guid leaseToken,
        string responseBody,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        // Nonlocking probes establish IDs and mutable preconditions only. All writes happen after
        // the required Asset/workspace/run/job locks are acquired in a deterministic order.
        List<JobRow> probeJobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (probeJobs.Count != 1 || probeJobs[0].AnalysisRunId is null)
        {
            return TerminalInvalid();
        }

        JobRow probeJob = probeJobs[0];

        List<RunRow> probeRuns = await dbContext.Database.SqlQueryRaw<RunRow>(
            """
            SELECT r."Id", r."AssetId", r."AssetVersionId", r."State"::text AS "State",
                   r."ContentSha256", r."InputDigest", r."PolicyVersionId", r."PolicyDigest",
                   r."DeclarationDigest", r."MaterialDigest", r."SelectionGeneration",
                   r."ArchiveEvidenceId", r."MalwareEvidenceId", r."SecurityGeneration",
                   r."SecurityRecheckId", r."DeclarationRevisionId", r."MaterialRevisionId",
                   r."ServiceKey", r."TransportLeaseToken", r."ReservationGeneration",
                   r."AssetDraftWorkspaceId"
            FROM code_analysis_runs AS r
            WHERE r."Id" = {0}
            """, probeJob.AnalysisRunId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (probeRuns.Count != 1)
        {
            return TerminalInvalid();
        }

        RunRow probeRun = probeRuns[0];

        // Asset first so no Asset lock is obtained after job/run locks. Workspace, when present,
        // is locked next; run/job are locked only after mutable Asset/workspace state is owned.
        List<AssetDeletedRow> assetRows = await dbContext.Database.SqlQueryRaw<AssetDeletedRow>(
            """SELECT "Id", "DeletedAt", "AuthorId" FROM assets WHERE "Id" = {0} FOR UPDATE""",
            probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (assetRows.Count != 1)
        {
            return TerminalInvalid();
        }

        // Mutable deletion authorization is applied after replay resolution below.


        WorkspaceRow? lockedWorkspace = null;
        if (probeRun.AssetDraftWorkspaceId is { } workspaceId)
        {
            List<WorkspaceRow> workspaceRows = await dbContext.Database.SqlQueryRaw<WorkspaceRow>(
                """
                SELECT w."Id", w."AssetId", w."AssetVersionId",
                       w."SourceDeclarationHeadRevision", w."MaterialMetadataHeadRevision",
                       (SELECT d."Id" FROM asset_source_declaration_revisions AS d
                        WHERE d."WorkspaceId" = w."Id" AND d."Revision" = w."SourceDeclarationHeadRevision")
                           AS "SourceDeclarationRevisionId",
                       (SELECT d."ContentDigest" FROM asset_source_declaration_revisions AS d
                        WHERE d."WorkspaceId" = w."Id" AND d."Revision" = w."SourceDeclarationHeadRevision")
                           AS "SourceDeclarationContentDigest",
                       (SELECT m."Id" FROM asset_material_metadata_revisions AS m
                        WHERE m."WorkspaceId" = w."Id" AND m."Revision" = w."MaterialMetadataHeadRevision")
                           AS "MaterialMetadataRevisionId"
                       ,(SELECT m."ContentDigest" FROM asset_material_metadata_revisions AS m
                        WHERE m."WorkspaceId" = w."Id" AND m."Revision" = w."MaterialMetadataHeadRevision")
                           AS "MaterialMetadataContentDigest"
                FROM asset_draft_workspaces AS w WHERE w."Id" = {0} FOR UPDATE OF w
                """, workspaceId).ToListAsync(cancellationToken).ConfigureAwait(false);

            if (workspaceRows.Count != 1)
            {
                return TerminalInvalid();
            }

            lockedWorkspace = workspaceRows[0];
        }

        List<VersionRow> versions = await dbContext.Database.SqlQueryRaw<VersionRow>(
            """
            SELECT v."Id", v."ContentSha256", v."ContentLength", v."SafetyRestriction"::text AS "SafetyRestriction",
                   v."AnalysisSelectionGeneration", v."SelectedCodeAnalysisReportId"
            FROM asset_versions AS v
            WHERE v."Id" = {0} AND v."AssetId" = {1}
            FOR UPDATE
            """, probeJob.AssetVersionId, probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (versions.Count != 1)
        {
            return TerminalInvalid();
        }

        List<RunRow> runs = await dbContext.Database.SqlQueryRaw<RunRow>(
            """
            SELECT r."Id", r."AssetId", r."AssetVersionId", r."State"::text AS "State",
                   r."ContentSha256", r."InputDigest", r."PolicyVersionId", r."PolicyDigest",
                   r."DeclarationDigest", r."MaterialDigest", r."SelectionGeneration",
                   r."ArchiveEvidenceId", r."MalwareEvidenceId", r."SecurityGeneration",
                   r."SecurityRecheckId", r."DeclarationRevisionId", r."MaterialRevisionId",
                   r."ServiceKey", r."TransportLeaseToken", r."ReservationGeneration",
                   r."AssetDraftWorkspaceId"
            FROM code_analysis_runs AS r
            WHERE r."Id" = {0}
            FOR UPDATE
            """, probeJob.AnalysisRunId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (runs.Count != 1)
        {
            return TerminalInvalid();
        }

        RunRow run = runs[0];

        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            FOR UPDATE OF j
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (jobs.Count != 1)
        {
            return TerminalInvalid();
        }

        JobRow job = jobs[0];
        var replayState = string.Equals(job.Status, "SUCCEEDED", StringComparison.Ordinal)
            && string.Equals(run.State, "COMPLETED", StringComparison.Ordinal);

        if (!replayState && assetRows[0].DeletedAt is not null)
        {
            return TerminalInvalid();
        }

        // Resolve exact immutable replay before any mutable lifecycle checks or new writes.
        if (replayState)
        {
            CodeAnalysisPolicyVersion? replayPolicyRow = await dbContext.CodeAnalysisPolicyVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == run.PolicyVersionId, cancellationToken).ConfigureAwait(false);

            if (TryParseExecutionFailure(responseBody, run, job, replayPolicyRow, out _, out _))
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
            }

            CodeAnalysisRun replayRunEntity = await dbContext.CodeAnalysisRuns
                .FirstAsync(r => r.Id == run.Id, cancellationToken).ConfigureAwait(false);
            CodeAnalysisPolicyVersion? replayPolicy = replayPolicyRow;

            List<ExistingReport> replayExisting = await dbContext.Database.SqlQueryRaw<ExistingReport>(
                """
                SELECT h."Id", h."PythonReportId", h."ReportDigest"
                FROM code_analysis_report_headers AS h
                WHERE h."AnalysisRunId" = {0} AND h."IsFinalized"
                LIMIT 1
                """, run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

            if (replayExisting.Count != 1)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
            }

            ValidatedCodeAnalysisReport replayValidated;
            try
            {
                CodeAnalysisReuseSourceBinding? reuseSource = await ReadReuseSource(
                    job.Id, replayRunEntity, replayPolicy ?? throw new CodeAnalysisValidationException("Pinned policy missing."), cancellationToken).ConfigureAwait(false);
                replayValidated = validator.ValidateCompleted(
                    responseBody,
                    replayRunEntity,
                    replayPolicy ?? throw new CodeAnalysisValidationException("Pinned policy missing."),
                    reuseSource);
            }
            catch (CodeAnalysisValidationException)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
            }

            if (string.Equals(replayExisting[0].PythonReportId, replayValidated.PythonReportId, StringComparison.Ordinal)
                && string.Equals(replayExisting[0].ReportDigest, replayValidated.ReportDigest, StringComparison.Ordinal))
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.CommittedSucceeded();
            }

            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
        }

        // Refresh lease state after all required locks are owned. A lease that expired while
        // waiting for Asset/workspace cannot authorize new writes.
        DateTimeOffset dbNow = await ReadClock(cancellationToken).ConfigureAwait(false);

        if (!replayState && (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt is null
            || job.LeaseExpiresAt <= dbNow))
        {
            return TerminalInvalid();
        }

        if ((!replayState && run.State != "RUNNING")
            || run.AssetId != job.AssetId
            || run.AssetVersionId != job.AssetVersionId)
        {
            return TerminalInvalid();
        }

        VersionRow version = versions[0];
        if (!replayState && version.SafetyRestriction != "NONE")
        {
            return TerminalInvalid();
        }

        // Execution-stage failure envelopes never admit a report. They reset a retryable run to
        // QUEUED for its bounded attempts and fail it otherwise. The job transition itself is left
        // to the worker so attempts and delays stay coherent.
        CodeAnalysisPolicyVersion? failurePolicy = await dbContext.CodeAnalysisPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == run.PolicyVersionId, cancellationToken)
            .ConfigureAwait(false);

        if (TryParseExecutionFailure(responseBody, run, job, failurePolicy, out var failureCode, out var retryable))
        {
            if (retryable && job.AttemptCount < job.MaxAttempts)
            {
                var retryDelay = TimeSpan.FromSeconds(30);
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE code_analysis_runs
                    SET "State" = 'QUEUED',
                        "TransportLeaseToken" = NULL,
                        "ReservationGeneration" = NULL,
                        "HardDeadlineUtc" = NULL,
                        "ErrorCode" = {failureCode},
                        "UpdatedAt" = {dbNow}
                    WHERE "Id" = {run.Id} AND "State" = 'RUNNING'
                    """, cancellationToken).ConfigureAwait(false);

                var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE asset_processing_jobs
                    SET "Status" = 'RETRY_SCHEDULED',
                        "Stage" = 'RETRY_SCHEDULED',
                        "ErrorCode" = {failureCode},
                        "ErrorSummary" = {ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID)},
                        "AvailableAt" = {dbNow + retryDelay},
                        "LeaseOwner" = NULL,
                        "LeaseToken" = NULL,
                        "LeaseExpiresAt" = NULL,
                        "TransportLeaseToken" = NULL,
                        "UpdatedAt" = {dbNow}
                    WHERE "Id" = {job.Id}
                      AND "Status" = 'RUNNING'
                      AND "LeaseToken" = {leaseToken}
                      AND "LeaseExpiresAt" > {dbNow}
                    """, cancellationToken).ConfigureAwait(false);

                if (updated == 0)
                {
                    await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return TerminalInvalid();
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.CommittedRetryScheduled();
            }

            // The failure is genuine and operational: the exact service code the envelope carried is
            // preserved on the run and the job, so operators see which tool, index or cleanup fault
            // actually stopped the run. ANALYSIS_RESPONSE_INVALID is reserved for an envelope the
            // server refused, never for a real service failure.
            return await FailRun(
                tx,
                job,
                run,
                failureCode,
                "The analysis execution failed in the analysis service.",
                dbNow,
                retryable,
                cancellationToken).ConfigureAwait(false);
        }

        // Replay: the same run already finalized with the same input digest returns
        // the persisted report without duplicate effects and without rerunning tools.
        List<ExistingReport> existing = await dbContext.Database.SqlQueryRaw<ExistingReport>(
            """
            SELECT h."Id", h."PythonReportId", h."ReportDigest"
            FROM code_analysis_report_headers AS h
            WHERE h."AnalysisRunId" = {0} AND h."IsFinalized"
            LIMIT 1
            """, run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        CodeAnalysisRun runEntity = await dbContext.CodeAnalysisRuns
            .FirstAsync(r => r.Id == run.Id, cancellationToken).ConfigureAwait(false);

        CodeAnalysisPolicyVersion? policy = await dbContext.CodeAnalysisPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == run.PolicyVersionId, cancellationToken).ConfigureAwait(false);

        if (policy is null)
        {
            return await FailRun(tx, job, run, "ANALYSIS_RESPONSE_INVALID", "Pinned policy is unavailable.", dbNow, cancellationToken).ConfigureAwait(false);
        }

        // Admission is held to the same current-proof contract scheduling was: the exact evidence
        // pair this run was bound to must still be the selected security generation, observed by
        // succeeded producers under the approved policy. An already-admitted report is never
        // re-admitted, so the replay paths above deliberately do not re-run this check: they return
        // the persisted report without creating new authority.
        SecurityProofVerdict proof = await CurrentSecurityProof.Verify(
            dbContext,
            new SecurityProofExpectation(
                job.AssetId,
                job.AssetVersionId,
                run.ContentSha256,
                run.SecurityRecheckId,
                run.ArchiveEvidenceId,
                run.MalwareEvidenceId,
                run.SecurityGeneration),
            version.ContentLength,
            dbNow,
            cancellationToken).ConfigureAwait(false);

        if (!proof.Accepted)
        {
            logger.LogWarning(
                "Analysis admission refused for run {RunId}: the bound security proof is not current ({Rejection}).",
                run.Id,
                proof.Reason);
            return await FailRun(
                tx,
                job,
                run,
                "ANALYSIS_RESPONSE_INVALID",
                "The security proof bound to this run is no longer admissible.",
                dbNow,
                cancellationToken).ConfigureAwait(false);
        }

        ValidatedCodeAnalysisReport validated;
        try
        {
            CodeAnalysisReuseSourceBinding? reuseSource = await ReadReuseSource(job.Id, runEntity, policy, cancellationToken).ConfigureAwait(false);
            validated = validator.ValidateCompleted(responseBody, runEntity, policy, reuseSource);
        }
        catch (CodeAnalysisValidationException ex)
        {
            logger.LogWarning(ex, "Analysis response for run {RunId} failed validation.", run.Id);
            return await FailRun(tx, job, run, "ANALYSIS_RESPONSE_INVALID", "The analysis response failed validation.", dbNow, cancellationToken).ConfigureAwait(false);
        }

        // Altered same-run response conflicts: the run already produced a different report.
        if (existing.Count == 1)
        {
            if (string.Equals(existing[0].PythonReportId, validated.PythonReportId, StringComparison.Ordinal)
                && string.Equals(existing[0].ReportDigest, validated.ReportDigest, StringComparison.Ordinal))
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.CommittedSucceeded();
            }

            logger.LogWarning("Altered replay for run {RunId} conflicts with the persisted report.", run.Id);
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
        }

        // Compare-and-swap on the current input: selection is allowed only while the run's input
        // tuple is still the live one. The selection generation and content hash are checked above;
        // here the declaration and material revisions the run was built from must still be the
        // workspace heads. A seller edit made after the run started makes the completed report
        // historical, so it can never be selected as describing the current offering.
        var isCurrent = version.AnalysisSelectionGeneration == run.SelectionGeneration
            && string.Equals(version.ContentSha256, run.ContentSha256, StringComparison.Ordinal);

        if (isCurrent && lockedWorkspace is not null)
        {
            isCurrent = lockedWorkspace.AssetId == run.AssetId
                && lockedWorkspace.AssetVersionId == run.AssetVersionId
                && (lockedWorkspace.SourceDeclarationHeadRevision == 0
                    ? run.DeclarationRevisionId is null
                        && string.Equals(run.DeclarationDigest, ProductDigests.EmptyProjectionDigest(nameof(AssetSourceDeclarationRevision)), StringComparison.Ordinal)
                    : lockedWorkspace.SourceDeclarationRevisionId == run.DeclarationRevisionId
                        && string.Equals(lockedWorkspace.SourceDeclarationContentDigest, run.DeclarationDigest, StringComparison.Ordinal))
                && (lockedWorkspace.MaterialMetadataHeadRevision == 0
                    ? run.MaterialRevisionId is null
                        && string.Equals(run.MaterialDigest, ProductDigests.EmptyProjectionDigest(nameof(AssetMaterialMetadataRevision)), StringComparison.Ordinal)
                    : lockedWorkspace.MaterialMetadataRevisionId == run.MaterialRevisionId
                        && string.Equals(lockedWorkspace.MaterialMetadataContentDigest, run.MaterialDigest, StringComparison.Ordinal));
        }

        var disposition = isCurrent ? "SELECTED" : "HISTORICAL_STALE_INPUT";
        var reportId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO code_analysis_report_headers (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "PolicyVersion", "InputRevision",
                "Purpose", "ReviewEligible", "IsFinalized", "FinalizedAt", "ReportSchemaVersion",
                "AnalysisRunId", "PythonReportId", "ReportDigest", "SelectionGeneration",
                "CoverageLevel", "AnalyzedFileCount", "OmissionCount", "FindingCount", "Disposition", "RetrievalSummaryJson",
                "CreatedAt", "UpdatedAt"
            ) VALUES (
                {reportId}, {job.AssetId}, {job.AssetVersionId}, {run.ContentSha256}, {policy.PolicyKey}, {run.SelectionGeneration},
                'PRODUCTION', true, true, {dbNow}, 1,
                {run.Id}, {validated.PythonReportId}, {validated.ReportDigest}, {run.SelectionGeneration},
                {validated.CoverageLevel}, {validated.AnalyzedFileCount}, {validated.OmissionCount}, {validated.FindingCount}, {disposition}, CAST({validated.RetrievalSummaryJson} AS jsonb),
                {dbNow}, {dbNow}
            )
            """, cancellationToken).ConfigureAwait(false);

        var persistedStageIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (ValidatedReportStage stage in validated.Stages)
        {
            var stageId = Guid.NewGuid();
            persistedStageIds.Add(stage.Stage, stageId);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO code_analysis_report_stages (
                    "Id", "ReportId", "Stage", "Applicability", "Outcome", "ErrorCode", "Retryable",
                    "DurationMilliseconds", "InputSha256", "OutputSha256", "ToolIdentityJson", "CountsJson",
                    "ReuseParentRunId", "ReuseParentStageId", "CreatedAt", "UpdatedAt"
                ) VALUES (
                    {stageId}, {reportId}, {stage.Stage}, {stage.Applicability}, {stage.Outcome}, {stage.ErrorCode}, {stage.Retryable},
                    {stage.DurationMilliseconds}, {stage.InputSha256}, {stage.OutputSha256}, CAST({stage.ToolIdentityJson} AS jsonb), CAST({stage.CountsJson} AS jsonb),
                    {stage.ReuseParentRunId}, {stage.ReuseParentStageId}, {dbNow}, {dbNow}
                )
                """, cancellationToken).ConfigureAwait(false);
        }

        foreach (ValidatedAnalysisReuseArtifact artifact in validated.ReuseArtifacts)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO code_analysis_reuse_artifacts (
                    "Id", "SourceReportId", "SourceRunId", "SourceStageId", "Stage",
                    "ArtifactDigest", "ArtifactJson", "PayloadBytes", "CreatedAt", "UpdatedAt"
                ) VALUES (
                    {Guid.NewGuid()}, {reportId}, {run.Id}, {persistedStageIds[artifact.Stage]}, {artifact.Stage},
                    {artifact.ArtifactDigest}, CAST({artifact.ArtifactJson} AS jsonb), {artifact.PayloadBytes}, {dbNow}, {dbNow}
                )
                """, cancellationToken).ConfigureAwait(false);
        }

        foreach (ValidatedReportFinding finding in validated.Findings)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO code_analysis_report_findings (
                    "Id", "ReportId", "FindingKey", "FindingType", "OwnScopePath", "OwnExcerpt", "OwnStartByte", "OwnEndByte",
                    "OwnStartLine", "OwnEndLine",
                    "ForeignSourceId", "ForeignOrigin", "ForeignSensitivity", "PublicAttributionJson",
                    "TokenScore", "SemanticScore", "TokenRank", "SemanticRank", "RetrievalEvidenceJson",
                    "ReasonText", "RequestedAction", "CreatedAt", "UpdatedAt"
                ) VALUES (
                    {Guid.NewGuid()}, {reportId}, {finding.FindingKey}, {finding.FindingType}, {finding.OwnScopePath}, {finding.OwnExcerpt}, {finding.OwnStartByte}, {finding.OwnEndByte},
                    {finding.OwnStartLine}, {finding.OwnEndLine},
                    {finding.ForeignSourceId}, {finding.ForeignOrigin}, {finding.ForeignSensitivity}, CAST({finding.PublicAttributionJson} AS jsonb),
                    {finding.TokenScore}, {finding.SemanticScore}, {finding.TokenRank}, {finding.SemanticRank}, CAST({finding.RetrievalEvidenceJson} AS jsonb),
                    {finding.ReasonText}, {finding.RequestedAction}, {dbNow}, {dbNow}
                )
                """, cancellationToken).ConfigureAwait(false);
        }

        var leaseDigest = Sha256Hex(job.TransportLeaseToken ?? "");
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO code_analysis_report_admissions (
                "Id", "ReportId", "AdmissionKind", "AdmittedBy", "AdmittedAt", "ReportDigest",
                "PythonReportId", "InputDigest", "ServiceKey", "ReservationSlotNumber", "ReservationGeneration",
                "ReservationLeaseTokenDigest", "ReviewEligible", "ReviewEligibilityReason",
                "CreatedAt", "UpdatedAt"
            ) VALUES (
                {Guid.NewGuid()}, {reportId}, 'SERVER_VALIDATED', 'assetblock-dotnet-finalizer', {dbNow}, {validated.ReportDigest},
                {validated.PythonReportId}, {run.InputDigest}, {run.ServiceKey}, 0, {run.ReservationGeneration ?? 0},
                {leaseDigest}, true, {"Eligible for explicit human review; no publication without a moderator decision."},
                {dbNow}, {dbNow}
            )
            """, cancellationToken).ConfigureAwait(false);

        if (isCurrent)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE asset_versions
                SET "SelectedCodeAnalysisReportId" = {reportId}
                WHERE "Id" = {job.AssetVersionId} AND "AssetId" = {job.AssetId}
                """, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "State" = 'COMPLETED',
                "Disposition" = {(isCurrent ? "SELECTED" : "HISTORICAL_STALE_INPUT")},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {run.Id} AND "State" = 'RUNNING'
            """, cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'SUCCEEDED',
                "Stage" = 'SUCCEEDED',
                "CompletedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "TransportLeaseToken" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {jobId} AND "Status" = 'RUNNING' AND "LeaseToken" = {leaseToken}
            """, cancellationToken).ConfigureAwait(false);

        await WriteAudit(run, reportId, dbNow, cancellationToken).ConfigureAwait(false);
        await EnqueueNotification(job, assetRows[0].AuthorId, dbNow, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AssetProcessingJobOutcome.CommittedSucceeded();
    }

    public async Task<AssetProcessingJobOutcome> FinalizeFailure(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        string safeSummary,
        bool ownerRetryable,
        CancellationToken cancellationToken = default)
    {
        var boundedSummary = AssetProcessingJobStore.BoundErrorSummary(safeSummary);
        await using IDbContextTransaction tx = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        List<JobRow> probeJobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (probeJobs.Count != 1 || probeJobs[0].AnalysisRunId is not { } runId)
        {
            return TerminalInvalid();
        }

        JobRow probeJob = probeJobs[0];
        List<RunRow> probeRuns = await dbContext.Database.SqlQueryRaw<RunRow>(
            """
            SELECT r."Id", r."AssetId", r."AssetVersionId", r."State"::text AS "State",
                   r."ContentSha256", r."InputDigest", r."PolicyVersionId", r."PolicyDigest",
                   r."DeclarationDigest", r."MaterialDigest", r."SelectionGeneration",
                   r."ArchiveEvidenceId", r."MalwareEvidenceId", r."SecurityGeneration",
                   r."SecurityRecheckId", r."ServiceKey", r."TransportLeaseToken",
                   r."ReservationGeneration", r."AssetDraftWorkspaceId",
                   r."DeclarationRevisionId", r."MaterialRevisionId"
            FROM code_analysis_runs AS r
            WHERE r."Id" = {0}
            """, runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (probeRuns.Count != 1)
        {
            return TerminalInvalid();
        }

        RunRow probeRun = probeRuns[0];
        List<AssetDeletedRow> assets = await dbContext.Database.SqlQueryRaw<AssetDeletedRow>(
            """SELECT "Id", "DeletedAt", "AuthorId" FROM assets WHERE "Id" = {0} FOR UPDATE""",
            probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (assets.Count != 1)
        {
            return TerminalInvalid();
        }

        if (probeRun.AssetDraftWorkspaceId is { } workspaceId)
        {
            List<WorkspaceRow> workspaces = await dbContext.Database.SqlQueryRaw<WorkspaceRow>(
                """SELECT "Id", "AssetId", "AssetVersionId", "SourceDeclarationHeadRevision", "MaterialMetadataHeadRevision" FROM asset_draft_workspaces WHERE "Id" = {0} FOR UPDATE""",
                workspaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (workspaces.Count != 1)
            {
                return TerminalInvalid();
            }
        }

        List<VersionRow> versions = await dbContext.Database.SqlQueryRaw<VersionRow>(
            """SELECT "Id", "ContentSha256", "ContentLength", "SafetyRestriction"::text AS "SafetyRestriction", "AnalysisSelectionGeneration", "SelectedCodeAnalysisReportId" FROM asset_versions WHERE "Id" = {0} AND "AssetId" = {1} FOR UPDATE""",
            probeJob.AssetVersionId, probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Count != 1)
        {
            return TerminalInvalid();
        }

        List<RunRow> runs = await dbContext.Database.SqlQueryRaw<RunRow>(
            """
            SELECT r."Id", r."AssetId", r."AssetVersionId", r."State"::text AS "State",
                   r."ContentSha256", r."InputDigest", r."PolicyVersionId", r."PolicyDigest",
                   r."DeclarationDigest", r."MaterialDigest", r."SelectionGeneration",
                   r."ArchiveEvidenceId", r."MalwareEvidenceId", r."SecurityGeneration",
                   r."SecurityRecheckId", r."ServiceKey", r."TransportLeaseToken",
                   r."ReservationGeneration", r."AssetDraftWorkspaceId",
                   r."DeclarationRevisionId", r."MaterialRevisionId"
            FROM code_analysis_runs AS r
            WHERE r."Id" = {0}
            FOR UPDATE
            """, runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (runs.Count != 1 || jobs.Count != 1)
        {
            return TerminalInvalid();
        }

        RunRow run = runs[0];
        JobRow job = jobs[0];
        DateTimeOffset dbNow = await ReadClock(cancellationToken).ConfigureAwait(false);
        if (run.Id != runId || run.State != "RUNNING"
            || job.Status != "RUNNING" || job.AnalysisRunId != run.Id
            || job.AssetId != run.AssetId || job.AssetVersionId != run.AssetVersionId
            || job.LeaseToken != leaseToken || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= dbNow)
        {
            return TerminalInvalid();
        }

        var retryableForOwner = ownerRetryable
            && versions[0].SafetyRestriction == nameof(AssetVersionSafetyRestriction.NONE);
        var runUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "State" = 'FAILED',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "OwnerRetryable" = {retryableForOwner},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {run.Id} AND "State" = 'RUNNING'
            """, cancellationToken).ConfigureAwait(false);
        var jobUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "CompletedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "TransportLeaseToken" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {job.Id} AND "Status" = 'RUNNING' AND "LeaseToken" = {leaseToken}
            """, cancellationToken).ConfigureAwait(false);
        if (runUpdated != 1 || jobUpdated != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return TerminalInvalid();
        }

        var metadata = JsonSerializer.Serialize(new { runId = run.Id, errorCode, ownerRetryable = retryableForOwner });
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_logs ("OccurredAt", "ActorType", "Action", "Outcome", "ResourceType", "ResourceId", "MetadataJson")
            VALUES ({dbNow}, 'SYSTEM', 'CODE_ANALYSIS_FAILED', 'FAILURE', 'CodeAnalysisRun', {run.Id.ToString()}, CAST({metadata} AS jsonb))
            """, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AssetProcessingJobOutcome.CommittedFailed();
    }

    private Task<AssetProcessingJobOutcome> FailRun(
        IDbContextTransaction tx,
        JobRow job,
        RunRow run,
        string errorCode,
        string safeSummary,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken) => FailRun(
            tx, job, run, errorCode, safeSummary, dbNow, false, cancellationToken);

    private async Task<AssetProcessingJobOutcome> FailRun(
        IDbContextTransaction tx,
        JobRow job,
        RunRow run,
        string errorCode,
        string safeSummary,
        DateTimeOffset dbNow,
        bool ownerRetryable,
        CancellationToken cancellationToken)
    {
        // Never mutate or audit a run that is not currently live/in the RUNNING state.
        var runUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "State" = 'FAILED',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {safeSummary},
                "OwnerRetryable" = {ownerRetryable},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {run.Id} AND "State" = 'RUNNING'
            """, cancellationToken).ConfigureAwait(false);

        var jobUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {safeSummary},
                "CompletedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "TransportLeaseToken" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {job.Id} AND "Status" = 'RUNNING'
            """, cancellationToken).ConfigureAwait(false);

        if (runUpdated != 1 || jobUpdated != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
        }

        if (runUpdated > 0 || jobUpdated > 0)
        {
            await WriteAudit(run, null, dbNow, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AssetProcessingJobOutcome.CommittedFailed();
    }

    private static AssetProcessingJobOutcome TerminalInvalid()
    {
        return AssetProcessingJobOutcome.Terminal(
            ErrorCodes.ANALYSIS_RESPONSE_INVALID,
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID));
    }

    private async Task<CodeAnalysisReuseSourceBinding?> ReadReuseSource(
        Guid jobId,
        CodeAnalysisRun currentRun,
        CodeAnalysisPolicyVersion policy,
        CancellationToken cancellationToken)
    {
        var payloadJson = await dbContext.Database.SqlQueryRaw<string>(
            "SELECT \"Payload\"::text AS \"Value\" FROM asset_processing_jobs WHERE \"Id\" = {0}", jobId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AssetProcessingPayload payload = AssetProcessingSerializer.DeserializePayload(
                AssetProcessingJobType.CODE_ANALYSIS, payloadJson);
            var analysisPayload = (CodeAnalysisPayload)payload;
            if (analysisPayload.ReuseSourceRunId is not { } sourceRunId)
            {
                if (analysisPayload.ReuseSourceInputDigest is not null || analysisPayload.ReuseSourceMaterialDigest is not null)
                {
                    throw new CodeAnalysisValidationException("Partial reuse source binding.");
                }
                return null;
            }

            CodeAnalysisRun? source = await dbContext.CodeAnalysisRuns
                .FromSqlInterpolated($"SELECT * FROM code_analysis_runs WHERE \"Id\" = {sourceRunId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (source is null
                || source.State != CodeAnalysisRunState.COMPLETED
                || source.Disposition != CodeAnalysisReportDisposition.SELECTED
                || source.SelectionGeneration >= currentRun.SelectionGeneration
                || source.AssetId != currentRun.AssetId
                || source.AssetVersionId != currentRun.AssetVersionId
                || source.PolicyVersionId != currentRun.PolicyVersionId
                || !string.Equals(source.PolicyDigest, currentRun.PolicyDigest, StringComparison.Ordinal)
                || !string.Equals(source.ContentSha256, currentRun.ContentSha256, StringComparison.Ordinal)
                || !string.Equals(source.MaterialDigest, currentRun.MaterialDigest, StringComparison.Ordinal)
                || source.SecurityGeneration != currentRun.SecurityGeneration
                || source.ArchiveEvidenceId != currentRun.ArchiveEvidenceId
                || source.MalwareEvidenceId != currentRun.MalwareEvidenceId
                || string.Equals(source.DeclarationDigest, currentRun.DeclarationDigest, StringComparison.Ordinal)
                || !string.Equals(source.InputDigest, analysisPayload.ReuseSourceInputDigest, StringComparison.Ordinal)
                || !string.Equals(source.MaterialDigest, analysisPayload.ReuseSourceMaterialDigest, StringComparison.Ordinal))
            {
                throw new CodeAnalysisValidationException("Reuse parent run is not an eligible immutable source.");
            }

            CodeAnalysisReportHeader? header = await dbContext.CodeAnalysisReportHeaders
                .FromSqlInterpolated($"SELECT * FROM code_analysis_report_headers WHERE \"AnalysisRunId\" = {source.Id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(h => h.IsFinalized && h.Disposition == CodeAnalysisReportDisposition.SELECTED
                    && h.SelectionGeneration == source.SelectionGeneration && h.ReportDigest != null, cancellationToken)
                .ConfigureAwait(false);
            CodeAnalysisReportAdmission? admission = header is null ? null : await dbContext.CodeAnalysisReportAdmissions
                .FromSqlInterpolated($"SELECT * FROM code_analysis_report_admissions WHERE \"ReportId\" = {header.Id} FOR SHARE")
                .AsNoTracking().SingleOrDefaultAsync(a => a.AdmissionKind == CodeAnalysisAdmissionKind.SERVER_VALIDATED
                    && a.ReviewEligible == header.ReviewEligible && a.ReportDigest == header.ReportDigest
                    && a.InputDigest == source.InputDigest, cancellationToken).ConfigureAwait(false);
            if (header is null || admission is null
                || header.AnalysisRunId != source.Id
                || header.AssetId != source.AssetId
                || header.AssetVersionId != source.AssetVersionId
                || header.ContentSha256 != source.ContentSha256
                || header.PolicyVersion != policy.PolicyKey
                || header.Purpose != CodeAnalysisReportPurpose.PRODUCTION
                || header.PythonReportId is null
                || admission.PythonReportId != header.PythonReportId
                || !ProductDigests.IsLowercaseSha256Hex(header.PythonReportId))
            {
                throw new CodeAnalysisValidationException("Reuse parent report lacks immutable server admission.");
            }

            List<CodeAnalysisReuseArtifact> artifacts = await dbContext.CodeAnalysisReuseArtifacts
                .FromSqlInterpolated($"SELECT * FROM code_analysis_reuse_artifacts WHERE \"SourceRunId\" = {source.Id} AND \"SourceReportId\" = {header.Id} FOR SHARE")
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            string[] expectedStages = ["EXTRACTION", "TOKEN", "SEMANTIC"];
            if (artifacts.Count != expectedStages.Length
                || artifacts.Select(a => a.Stage).Distinct(StringComparer.Ordinal).Count() != expectedStages.Length
                || !expectedStages.All(name => artifacts.Any(a => a.Stage == name)))
            {
                throw new CodeAnalysisValidationException("Reuse parent artifact set is incomplete or ambiguous.");
            }

            var bound = new List<CodeAnalysisReuseStageArtifact>(expectedStages.Length);
            foreach (var name in expectedStages)
            {
                CodeAnalysisReuseArtifact artifact = artifacts.Single(a => a.Stage == name);
                CodeAnalysisReportStage? stage = await dbContext.CodeAnalysisReportStages
                    .FromSqlInterpolated($"SELECT * FROM code_analysis_report_stages WHERE \"Id\" = {artifact.SourceStageId} FOR SHARE")
                    .AsNoTracking().SingleOrDefaultAsync(s => s.ReportId == header.Id && s.Stage == name
                        && s.Outcome == CodeAnalysisStageOutcome.SUCCEEDED, cancellationToken).ConfigureAwait(false);
                if (stage is null || stage.OutputSha256 != artifact.ArtifactDigest
                    || !AnalysisReservationReader.TryVerifyArtifactBinding(artifact, source, policy))
                {
                    throw new CodeAnalysisValidationException("Reuse parent stage or artifact binding is invalid.");
                }
                bound.Add(new CodeAnalysisReuseStageArtifact(name, header.Id.ToString("D"), stage.Id.ToString("D"), artifact.ArtifactDigest, artifact.ArtifactJson));
            }

            return new CodeAnalysisReuseSourceBinding(source.Id.ToString("D"), source.InputDigest,
                source.DeclarationDigest, source.MaterialDigest, bound);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new CodeAnalysisValidationException("Analysis job payload is invalid.", ex);
        }
    }

    /// <summary>
    /// The exact execution codes the analysis service can fail a run with. A failure envelope with any
    /// other code is not a service failure of this run and is refused as an invalid response.
    /// </summary>
    private static readonly HashSet<string> _serviceFailureCodes = new(StringComparer.Ordinal)
    {
        "CANCELLED",
        "CLEANUP_FAILED",
        "DECLARATION_INVALID",
        "DIGEST_MISMATCH",
        "EXTRACTION_FAILED",
        "INDEX_NOT_READY",
        "INDEX_UNAVAILABLE",
        "INTERNAL_ERROR",
        "MALFORMED_METADATA",
        "MODEL_UNAVAILABLE",
        "POLICY_MISMATCH",
        "REFERENCE_GRANT_MISSING",
        "REFERENCE_MANIFEST_INVALID",
        "REPORT_TOO_LARGE",
        "RESERVATION_INVALID",
        "RUN_ID_INVALID",
        "SCAN_ERROR",
        "SECURITY_EVIDENCE_INVALID",
        "TOKENIZER_UNAVAILABLE",
        "TOOL_FAILED",
        "TOOL_MISSING",
        "TOOL_OUTPUT_INVALID",
        "TOOL_TIMEOUT",
        "UNKNOWN_METADATA_FIELD"
    };

    /// <summary>
    /// Recognizes the analysis service's execution-failure envelopes, and only the exact shapes its
    /// four failure producers actually emit: a stage failure carrying the observed stage records, the
    /// restated policy and the failed stage; a cancellation; an unhandled exception; and a cleanup
    /// failure that keeps the completed stage records. All four bind this exact run, input, content,
    /// asset and version; none carries admission authority, findings or a report identity, and none
    /// admits a report.
    /// <para>
    /// Anything else — a foreign run, an altered binding, fabricated authority, an unknown failure
    /// code, a missing or wrongly typed retryable flag, incoherent coverage, or a stage that did not
    /// actually fail — is refused as an invalid response instead of being allowed to reset or fail a
    /// live run.
    /// </para>
    /// </summary>
    private static bool TryParseExecutionFailure(
        string responseBody,
        RunRow run,
        JobRow job,
        CodeAnalysisPolicyVersion? policy,
        out string errorCode,
        out bool retryable)
    {
        errorCode = ErrorCodes.ANALYSIS_RESPONSE_INVALID;
        retryable = false;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        // Identity bindings every failure producer stamps: this exact run, input, bytes and subject.
        if (!TryReadString(root, "schema", out var schema)
            || !string.Equals(schema, ProductDigests.ANALYSIS_SCHEMA, StringComparison.Ordinal)
            || policy is null
            || !TryReadString(root, "serviceVersion", out var serviceVersion)
            || !string.Equals(serviceVersion, ReadPolicyServiceVersion(policy), StringComparison.Ordinal)
            || !TryReadString(root, "outcome", out var outcome)
            || (!string.Equals(outcome, "FAILED", StringComparison.Ordinal)
                && !string.Equals(outcome, "CANCELLED", StringComparison.Ordinal))
            || !TryReadString(root, "runId", out var runId)
            || !string.Equals(runId, run.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !TryReadString(root, "inputDigest", out var inputDigest)
            || !string.Equals(inputDigest, run.InputDigest, StringComparison.Ordinal)
            || !TryReadString(root, "contentSha256", out var contentSha256)
            || !string.Equals(contentSha256, run.ContentSha256, StringComparison.Ordinal)
            || !TryReadString(root, "assetId", out var assetId)
            || !string.Equals(assetId, job.AssetId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !TryReadString(root, "versionId", out var versionId)
            || !string.Equals(versionId, job.AssetVersionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A failed or cancelled run produced no findings and no report identity of any kind.
        if (!root.TryGetProperty("stages", out JsonElement stages)
            || stages.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("findings", out JsonElement findings)
            || findings.ValueKind != JsonValueKind.Array
            || findings.GetArrayLength() != 0
            || root.TryGetProperty("reportId", out _)
            || root.TryGetProperty("sellerSafe", out _))
        {
            return false;
        }

        // The service never carries admission authority on any failure shape.
        if (!root.TryGetProperty("admittedBy", out JsonElement admittedBy)
            || admittedBy.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        // The failure code must be one the service actually raises, and retryable must be a real
        // boolean rather than a missing or wrongly typed member silently defaulting to false.
        if (!TryReadString(root, "errorCode", out var code)
            || code!.Length > 64
            || !_serviceFailureCodes.Contains(code)
            || !root.TryGetProperty("retryable", out JsonElement retryElement)
            || (retryElement.ValueKind != JsonValueKind.True && retryElement.ValueKind != JsonValueKind.False)
            || !root.TryGetProperty("cleanupError", out JsonElement reportedCleanupError)
            || (reportedCleanupError.ValueKind != JsonValueKind.Null
                && (reportedCleanupError.ValueKind != JsonValueKind.String
                    || reportedCleanupError.GetString() != "CLEANUP_FAILED"))
            || !IsRealCoverage(root))
        {
            return false;
        }

        errorCode = code;
        retryable = retryElement.ValueKind == JsonValueKind.True;

        var hasStages = stages.GetArrayLength() > 0;

        // A cleanup failure keeps completed stage records and execution bindings, withholds findings,
        // and reports the supervisor's exact cleanup failure without falsifying the REPORT stage.
        if (string.Equals(code, "CLEANUP_FAILED", StringComparison.Ordinal))
        {
            return !retryable
                && string.Equals(outcome, "FAILED", StringComparison.Ordinal)
                && reportedCleanupError.ValueKind == JsonValueKind.String
                && HasExactProperties(root,
                    "schema", "serviceVersion", "outcome", "runId", "inputDigest", "contentSha256", "assetId", "versionId",
                    "policy", "securityEvidence", "reservation", "stages", "coverage", "findings", "findingOverflow",
                    "errorCode", "retryable", "failedStage", "admittedBy", "cleanupError")
                && TryReadString(root, "failedStage", out var cleanupStage)
                && string.Equals(cleanupStage, "REPORT", StringComparison.Ordinal)
                && string.Equals(reportedCleanupError.GetString(), "CLEANUP_FAILED", StringComparison.Ordinal)
                && IsFailureExecutionBinding(root, run)
                && root.TryGetProperty("findingOverflow", out JsonElement overflow)
                && overflow.ValueKind == JsonValueKind.Number
                && overflow.TryGetInt64(out var overflowCount)
                && overflowCount >= 0
                && IsPolicyBound(root, run, policy)
                && IsCoherentStageMatrix(stages, policy)
                && HasSuccessfulStageMatrix(stages, policy);
        }

        // Cancellation is its own terminal shape: no stage ran, no policy is restated and no cleanup
        // or failed stage is claimed. The service marks it retryable only when the cancellation came
        // from an operational error rather than from an ordinary cancellation.
        if (string.Equals(outcome, "CANCELLED", StringComparison.Ordinal))
        {
            return !hasStages
                && HasExactProperties(root,
                    "schema", "serviceVersion", "outcome", "runId", "inputDigest", "contentSha256", "assetId", "versionId",
                    "stages", "coverage", "findings", "errorCode", "retryable", "admittedBy", "cleanupError")
                && !root.TryGetProperty("policy", out _)
                && !root.TryGetProperty("failedStage", out _)
                && !root.TryGetProperty("findingOverflow", out _)
                && (!root.TryGetProperty("cleanupError", out JsonElement cancelledCleanup)
                    || cancelledCleanup.ValueKind == JsonValueKind.Null)
                && retryable == !string.Equals(code, "CANCELLED", StringComparison.Ordinal);
        }

        if (!root.TryGetProperty("failedStage", out JsonElement failedStageElement)
            || failedStageElement.ValueKind != JsonValueKind.String)
        {
            // The unhandled-exception producer reports no failed stage, restates no policy and is
            // never retryable; anything else in that shape is not this producer's output.
            return !hasStages
                && !retryable
                && !root.TryGetProperty("policy", out _)
                && HasExactProperties(root,
                    "schema", "serviceVersion", "outcome", "runId", "inputDigest", "contentSha256", "assetId", "versionId",
                    "stages", "coverage", "findings", "errorCode", "retryable", "failedStage", "admittedBy", "cleanupError")
                && root.TryGetProperty("failedStage", out JsonElement nullFailedStage)
                && nullFailedStage.ValueKind == JsonValueKind.Null;
        }

        if (hasStages)
        {
            // A stage failure names the stage that actually failed with that exact code, restates the
            // approved policy it ran under, and reports a coherent stage matrix: no unknown stage, no
            // duplicate, and every mandatory stage recorded exactly once.
            return IsFailedStageReported(stages, failedStageElement.GetString()!, code, retryable)
                && IsCoherentStageMatrix(stages, policy)
                && IsPolicyBound(root, run, policy)
                && reportedCleanupError.ValueKind == JsonValueKind.Null
                && HasExactProperties(root,
                    "schema", "serviceVersion", "outcome", "runId", "inputDigest", "contentSha256", "assetId", "versionId",
                    "policy", "stages", "coverage", "findings", "errorCode", "retryable", "failedStage", "admittedBy", "cleanupError");
        }

        // A failed outcome with no stage records and no failed stage is only the unhandled-exception
        // shape, which is never retryable.
        return false;
    }

    private static bool IsFailureExecutionBinding(JsonElement root, RunRow run)
    {
        if (!root.TryGetProperty("securityEvidence", out JsonElement security)
            || security.ValueKind != JsonValueKind.Object
            || !TryReadString(security, "contentSha256", out var contentSha256)
            || !string.Equals(contentSha256, run.ContentSha256, StringComparison.Ordinal)
            || !TryReadString(security, "archiveEvidenceId", out var archiveEvidenceId)
            || !string.Equals(archiveEvidenceId, run.ArchiveEvidenceId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !TryReadString(security, "malwareEvidenceId", out var malwareEvidenceId)
            || !string.Equals(malwareEvidenceId, run.MalwareEvidenceId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !security.TryGetProperty("securityGeneration", out JsonElement securityGeneration)
            || !securityGeneration.TryGetInt32(out var generation)
            || generation != run.SecurityGeneration
            || !root.TryGetProperty("reservation", out JsonElement reservation)
            || reservation.ValueKind != JsonValueKind.Object
            || !TryReadString(reservation, "serviceKey", out var serviceKey)
            || !string.Equals(serviceKey, run.ServiceKey, StringComparison.Ordinal)
            || !reservation.TryGetProperty("slotNumber", out JsonElement slotNumber)
            || !slotNumber.TryGetInt32(out var slot)
            || slot < 0
            || !reservation.TryGetProperty("generation", out JsonElement reservationGeneration)
            || !reservationGeneration.TryGetInt32(out var reservedGeneration))
        {
            return false;
        }

        return run.ReservationGeneration is { } expectedGeneration
            && expectedGeneration == reservedGeneration;
    }

    /// <summary>
    /// Every reported stage is one the service produces, each appears at most once, and each carries a
    /// well-formed record. An unknown, duplicated or malformed record is refused rather than ignored.
    /// </summary>
    private static string ReadPolicyServiceVersion(CodeAnalysisPolicyVersion policy)
    {
        try
        {
            using var manifest = JsonDocument.Parse(policy.ToolIdentitiesJson);
            return manifest.RootElement.TryGetProperty("serviceVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? ""
                    : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static bool IsCoherentStageMatrix(JsonElement stages, CodeAnalysisPolicyVersion? policy)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object
                || !TryReadString(stage, "stage", out var name)
                || !StageNames.Contains(name!)
                || !seen.Add(name!)
                || !TryReadString(stage, "applicability", out var applicability)
                || !string.Equals(applicability, "MANDATORY", StringComparison.Ordinal)
                || !TryReadString(stage, "outcome", out var outcome)
                || (outcome != "SUCCEEDED" && outcome != "FAILED" && !IsDisabledVerifier(stage, policy)))
            {
                return false;
            }

            if (!stage.TryGetProperty("retryable", out JsonElement retryable)
                || (retryable.ValueKind != JsonValueKind.True && retryable.ValueKind != JsonValueKind.False))
            {
                return false;
            }

            if (IsDisabledVerifier(stage, policy))
            {
                if (retryable.ValueKind == JsonValueKind.True)
                {
                    return false;
                }
                continue;
            }
            // A succeeded stage carries no error and is never retryable; a failed stage must carry one.
            if (string.Equals(outcome, "SUCCEEDED", StringComparison.Ordinal))
            {
                if (retryable.ValueKind == JsonValueKind.True
                    || (stage.TryGetProperty("errorCode", out JsonElement succeededError)
                        && succeededError.ValueKind != JsonValueKind.Null))
                {
                    return false;
                }
            }
            else if (!TryReadString(stage, "errorCode", out _))
            {
                return false;
            }
        }

        return true;
    }

    internal static IReadOnlySet<string> StageNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE", "UNION", "VERIFIER", "REPORT"
    };

    /// <summary>Coverage has to be a real, internally coherent coverage block.</summary>
    private static bool IsRealCoverage(JsonElement root)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadString(coverage, "level", out var level)
            || (level != "FULL" && level != "PARTIAL" && level != "UNSUPPORTED")
            || !coverage.TryGetProperty("analyzedFiles", out JsonElement analyzedFiles)
            || analyzedFiles.ValueKind != JsonValueKind.Number
            || !analyzedFiles.TryGetInt32(out var analyzed)
            || analyzed < 0
            || !coverage.TryGetProperty("omissions", out JsonElement omissions)
            || omissions.ValueKind != JsonValueKind.Array
            || !coverage.TryGetProperty("omissionOverflow", out JsonElement overflow)
            || overflow.ValueKind != JsonValueKind.Number
            || !overflow.TryGetInt32(out var overflowCount)
            || overflowCount < 0)
        {
            return false;
        }

        return level switch
        {
            "UNSUPPORTED" => analyzed == 0 && omissions.GetArrayLength() == 0 && overflowCount == 0,
            "FULL" => analyzed > 0 && omissions.GetArrayLength() == 0 && overflowCount == 0,
            _ => analyzed > 0 || omissions.GetArrayLength() > 0
        };
    }

    private static bool IsDisabledVerifier(JsonElement stage, CodeAnalysisPolicyVersion? policy) =>
        policy?.PolicyKey == ReviewAnalysisPolicy.KEY && policy.PolicyDigest == ReviewAnalysisPolicy.DIGEST
        && TryReadString(stage, "stage", out var name) && name == "VERIFIER"
        && TryReadString(stage, "outcome", out var outcome) && outcome == "NOT_APPLICABLE"
        && TryReadString(stage, "errorCode", out var code) && code == "POLICY_DISABLED";

    private static bool HasSuccessfulStageMatrix(JsonElement stages, CodeAnalysisPolicyVersion? policy)
    {
        if (stages.ValueKind != JsonValueKind.Array || stages.GetArrayLength() != StageNames.Count)
        {
            return false;
        }

        foreach (JsonElement stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object
                || !TryReadString(stage, "outcome", out var outcome)
                || (!string.Equals(outcome, "SUCCEEDED", StringComparison.Ordinal) && !IsDisabledVerifier(stage, policy)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactProperties(JsonElement value, params string[] expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                return false;
            }
        }

        return observed.SetEquals(expected);
    }

    /// <summary>The named failed stage must actually be reported as failed with that same code.</summary>
    private static bool IsFailedStageReported(JsonElement stages, string failedStage, string errorCode, bool retryable)
    {
        foreach (JsonElement stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object
                || !TryReadString(stage, "stage", out var name)
                || !string.Equals(name, failedStage, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryReadString(stage, "outcome", out var stageOutcome)
                || !string.Equals(stageOutcome, "FAILED", StringComparison.Ordinal)
                || !TryReadString(stage, "errorCode", out var stageCode)
                || !string.Equals(stageCode, errorCode, StringComparison.Ordinal)
                || !stage.TryGetProperty("retryable", out JsonElement stageRetryable)
                || (stageRetryable.ValueKind != JsonValueKind.True && stageRetryable.ValueKind != JsonValueKind.False))
            {
                return false;
            }

            return (stageRetryable.ValueKind == JsonValueKind.True) == retryable;
        }

        return false;
    }

    /// <summary>The restated policy must be the run's own approved policy and carry no authority.</summary>
    private static bool IsPolicyBound(JsonElement root, RunRow run, CodeAnalysisPolicyVersion? policy)
    {
        if (policy is null
            || !root.TryGetProperty("policy", out JsonElement policyJson)
            || policyJson.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryReadString(policyJson, "policyVersion", out var policyVersion)
            && string.Equals(policyVersion, policy.PolicyKey, StringComparison.Ordinal)
            && TryReadString(policyJson, "policyDigest", out var policyDigest)
            && string.Equals(policyDigest, run.PolicyDigest, StringComparison.Ordinal)
            && TryReadString(policyJson, "verifierMode", out var verifierMode)
            && string.Equals(verifierMode, policy.VerifierMode, StringComparison.Ordinal)
            && !policyJson.TryGetProperty("reviewEligible", out _);
    }


    private static bool TryReadString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString();
        return true;
    }

    public async Task<AssetProcessingJobOutcome> QuarantineIntegrity(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        List<JobRow> probeJobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (probeJobs.Count != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }

        JobRow probeJob = probeJobs[0];

        // Lock hierarchy: Asset -> version -> run -> job, with exact lease verification after every lock.
        List<AssetDeletedRow> assetRows = await dbContext.Database.SqlQueryRaw<AssetDeletedRow>(
            """SELECT "Id", "DeletedAt", "AuthorId" FROM assets WHERE "Id" = {0} FOR UPDATE""",
            probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (assetRows.Count != 1 || assetRows[0].DeletedAt is not null)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }

        List<Guid> versionRows = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM asset_versions WHERE "Id" = {0} AND "AssetId" = {1} FOR UPDATE""",
            probeJob.AssetVersionId, probeJob.AssetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (versionRows.Count != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }

        if (probeJob.AnalysisRunId is { } probeRunId)
        {
            List<Guid> runRows = await dbContext.Database.SqlQueryRaw<Guid>(
                """SELECT "Id" FROM code_analysis_runs WHERE "Id" = {0} FOR UPDATE""",
                probeRunId).ToListAsync(cancellationToken).ConfigureAwait(false);

            if (runRows.Count != 1)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.ARCHIVE_CORRUPT,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
            }
        }

        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT j."Id", j."AssetId", j."AssetVersionId", j."Status"::text AS "Status",
                   j."LeaseToken", j."LeaseExpiresAt", j."TransportLeaseToken", j."AnalysisRunId",
                   j."AttemptCount", j."MaxAttempts"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
            FOR UPDATE OF j
            """, jobId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (jobs.Count != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }

        JobRow job = jobs[0];

        // The exact lease must still be live after waiting for the Asset/version/run locks.
        DateTimeOffset dbNow = await ReadClock(cancellationToken).ConfigureAwait(false);
        if (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt is null
            || job.LeaseExpiresAt <= dbNow)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "SafetyRestriction" = 'INTEGRITY_MISMATCH',
                "SafetyRestrictionReason" = {errorCode},
                "SafetyRestrictedAt" = {dbNow}
            WHERE "Id" = {job.AssetVersionId} AND "AssetId" = {job.AssetId}
            """, cancellationToken).ConfigureAwait(false);

        if (job.AnalysisRunId is { } runId)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE code_analysis_runs
                SET "State" = 'FAILED',
                    "ErrorCode" = {errorCode},
                    "CompletedAt" = {dbNow},
                    "UpdatedAt" = {dbNow}
                WHERE "Id" = {runId} AND "State" = 'RUNNING'
                """, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = 'The decrypted analysis input failed integrity verification.',
                "CompletedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "TransportLeaseToken" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {jobId} AND "Status" = 'RUNNING' AND "LeaseToken" = {leaseToken}
            """, cancellationToken).ConfigureAwait(false);

        var metadata = JsonSerializer.Serialize(new { jobId, errorCode });
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_logs ("OccurredAt", "ActorType", "Action", "Outcome", "ResourceType", "ResourceId", "MetadataJson")
            VALUES ({dbNow}, 'SYSTEM', 'CODE_ANALYSIS_INTEGRITY_QUARANTINED', 'SUCCESS', 'AssetVersion', {job.AssetVersionId.ToString()}, CAST({metadata} AS jsonb))
            """, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AssetProcessingJobOutcome.CommittedFailed();
    }

    private Task<DateTimeOffset> ReadClock(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

    private async Task WriteAudit(RunRow run, Guid? reportId, DateTimeOffset dbNow, CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            runId = run.Id,
            reportId,
            inputDigest = run.InputDigest
        });

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_logs ("OccurredAt", "ActorType", "Action", "Outcome", "ResourceType", "ResourceId", "MetadataJson")
            VALUES ({dbNow}, 'SYSTEM', 'CODE_ANALYSIS_FINALIZED', 'SUCCESS', 'CodeAnalysisReport', {reportId.ToString()}, CAST({metadata} AS jsonb))
            """, ct).ConfigureAwait(false);
    }

    private async Task EnqueueNotification(JobRow job, Guid recipientUserId, DateTimeOffset dbNow, CancellationToken ct)
    {
        var notificationId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            assetId = job.AssetId,
            assetVersionId = job.AssetVersionId
        });
        var outboxPayload = JsonSerializer.Serialize(new
        {
            recipientUserId = recipientUserId,
            kind = NotificationKind.CODE_ANALYSIS_COMPLETED,
            hubMethod = NotificationHubMethods.CODE_ANALYSIS_COMPLETED,
            metadataJson = metadata
        });
        var payload = outboxPayload;

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount")
            VALUES ({outboxId}, {OutboxMessageTypes.NOTIFICATION_DISPATCH}, {payload}, {dbNow}, 0, 0, 0)
            """, ct).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_notifications ("Id", "RecipientUserId", "Kind", "MetadataJson", "CreatedAt", "SourceOutboxMessageId")
            VALUES ({notificationId}, {recipientUserId}, 'CODE_ANALYSIS_COMPLETED', CAST({metadata} AS jsonb), {dbNow}, {outboxId})
            """, ct).ConfigureAwait(false);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string Status { get; init; } = "";
        public Guid? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public string? TransportLeaseToken { get; init; }
        public Guid? AnalysisRunId { get; init; }
        public int AttemptCount { get; init; }
        public int MaxAttempts { get; init; }
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string State { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public string InputDigest { get; init; } = "";
        public Guid PolicyVersionId { get; init; }
        public string PolicyDigest { get; init; } = "";
        public string DeclarationDigest { get; init; } = "";
        public string MaterialDigest { get; init; } = "";
        public int SelectionGeneration { get; init; }
        public Guid ArchiveEvidenceId { get; init; }
        public Guid MalwareEvidenceId { get; init; }
        public int SecurityGeneration { get; init; }
        public Guid? SecurityRecheckId { get; init; }
        public string ServiceKey { get; init; } = "";
        public string? TransportLeaseToken { get; init; }
        public int? ReservationGeneration { get; init; }
        public Guid? AssetDraftWorkspaceId { get; init; }
        public Guid? DeclarationRevisionId { get; init; }
        public Guid? MaterialRevisionId { get; init; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public string ContentSha256 { get; init; } = "";
        public long ContentLength { get; init; }
        public string SafetyRestriction { get; init; } = "";
        public int AnalysisSelectionGeneration { get; init; }
        public Guid? SelectedCodeAnalysisReportId { get; init; }
    }

    /// <summary>Workspace input heads, locked before any admission decision reads them.</summary>
    private sealed class WorkspaceRow
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid? AssetVersionId { get; init; }
        public int SourceDeclarationHeadRevision { get; init; }
        public int MaterialMetadataHeadRevision { get; init; }
        public Guid? SourceDeclarationRevisionId { get; init; }
        public Guid? MaterialMetadataRevisionId { get; init; }
        public string? SourceDeclarationContentDigest { get; init; }
        public string? MaterialMetadataContentDigest { get; init; }
    }

    private sealed class ExistingReport
    {
        public Guid Id { get; init; }
        public string? PythonReportId { get; init; }
        public string? ReportDigest { get; init; }
    }

    private sealed class AssetDeletedRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset? DeletedAt { get; init; }
        public Guid AuthorId { get; init; }
    }
}
