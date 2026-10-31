using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Publication;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>Read-only gates reused by owner hints and the locked submission transaction.</summary>
internal static class ReviewSubmissionEligibility
{
    public static async Task<string?> BlockedReason(ApplicationDbContext db, AssetVersion version,
        AssetDraftWorkspace workspace, Guid? reportId, CancellationToken ct)
    {
        if (version.ProcessingStatus != AssetVersionProcessingStatus.READY
            || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE)
        {
            return ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE;
        }
        if (reportId is null || reportId != version.SelectedCodeAnalysisReportId)
        {
            return ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE;
        }
        CodeAnalysisReportHeader? report = await PublicationEligibilityQuery.AdmittedReports(db)
            .SingleOrDefaultAsync(h => h.Id == reportId && h.AssetId == version.AssetId
                && h.AssetVersionId == version.Id && h.Purpose == CodeAnalysisReportPurpose.PRODUCTION, ct);
        if (report is null || report.PolicyVersion != ReviewAnalysisPolicy.KEY || !report.ReviewEligible
            || report.ContentSha256 != version.ContentSha256
            || report.SelectionGeneration != version.AnalysisSelectionGeneration
            || report.Disposition != CodeAnalysisReportDisposition.SELECTED)
        {
            return ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED;
        }
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == report.AnalysisRunId, ct);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync(p => p.Id == run.PolicyVersionId, ct);
        if (policy.PolicyKey != ReviewAnalysisPolicy.KEY || policy.PolicyDigest != ReviewAnalysisPolicy.DIGEST
            || policy.VerifierMode != ReviewAnalysisPolicy.VERIFIER_MODE || policy.VerifierReason != ReviewAnalysisPolicy.VERIFIER_REASON
            || run.PolicyDigest != policy.PolicyDigest || run.AssetDraftWorkspaceId != workspace.Id
            || run.SelectionGeneration != version.AnalysisSelectionGeneration
            || !await db.CodeAnalysisReportAdmissions.AnyAsync(a => a.ReportId == report.Id
                && a.AdmissionKind == CodeAnalysisAdmissionKind.SERVER_VALIDATED && a.ReviewEligible
                && a.AdmittedBy == "assetblock-dotnet-finalizer" && a.ReportDigest == report.ReportDigest
                && a.PythonReportId == report.PythonReportId && a.InputDigest == run.InputDigest
                // Admission retains authenticated reservation provenance after capacity release clears the run lease.
                && a.ServiceKey == run.ServiceKey && a.ReservationGeneration > 0, ct))
        {
            return ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED;
        }
        AssetSourceDeclarationRevision? declaration = await db.AssetSourceDeclarationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.SourceDeclarationHeadRevision, ct);
        AssetMaterialMetadataRevision? material = await db.AssetMaterialMetadataRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.MaterialMetadataHeadRevision, ct);
        if (declaration is null || material is null || run.DeclarationRevisionId != declaration.Id
            || run.MaterialRevisionId != material.Id || run.DeclarationDigest != declaration.ContentDigest
            || run.MaterialDigest != material.ContentDigest)
        {
            return ErrorCodes.ERR_MODERATION_WORKSPACE_STALE;
        }
        if (!ModerationFoundationStore.IsDeclarationComplete(declaration.PayloadJson))
        {
            return ErrorCodes.ERR_DECLARATION_INCOMPLETE;
        }
        List<CodeAnalysisReportStage> stages = await db.CodeAnalysisReportStages.AsNoTracking().Where(s => s.ReportId == report.Id).ToListAsync(ct);
        if (stages.Count != CodeAnalysisReportFinalizer.StageNames.Count
            || CodeAnalysisReportFinalizer.StageNames.Where(name => name != "VERIFIER").Any(name => !stages.Any(s => s.Stage == name
                && s.Applicability == CodeAnalysisStageApplicability.MANDATORY && s.Outcome == CodeAnalysisStageOutcome.SUCCEEDED
                && s.ErrorCode == null && !s.Retryable))
            || !stages.Any(s => s.Stage == "VERIFIER" && s.Applicability == CodeAnalysisStageApplicability.MANDATORY
                && s.Outcome == CodeAnalysisStageOutcome.NOT_APPLICABLE
                && s.ErrorCode == "POLICY_DISABLED" && !s.Retryable))
        {
            return ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED;
        }
        DateTimeOffset now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").FirstAsync(ct);
        SecurityProofVerdict proof = await CurrentSecurityProof.Verify(db,
            new SecurityProofExpectation(version.AssetId, version.Id, version.ContentSha256, run.SecurityRecheckId,
                run.ArchiveEvidenceId, run.MalwareEvidenceId, run.SecurityGeneration), version.ContentLength, now, ct);
        if (!proof.Accepted)
        {
            return ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE;
        }
        if (report.CoverageLevel != CodeAnalysisCoverageLevel.FULL || report.OmissionCount != 0 || report.AnalyzedFileCount is not > 0)
        {
            return ErrorCodes.ERR_REVIEW_COVERAGE_REQUIRED;
        }
        // Retained candidates are moderator evidence. Only an explicit persisted response request
        // can create a seller obligation; this workflow does not create response requests yet.
        if (await db.ModerationSubmissions.AnyAsync(s => s.AssetVersionId == version.Id
                && (s.State == ModerationSubmissionState.SUBMITTED || s.State == ModerationSubmissionState.IN_REVIEW), ct))
        {
            return ErrorCodes.ERR_MODERATION_ACTIVE_CASE_EXISTS;
        }
        return null;
    }
}
