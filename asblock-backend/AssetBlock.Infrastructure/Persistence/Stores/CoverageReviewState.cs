using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed record CoverageTuple(Guid VersionId, string ContentSha256, Guid ReportId, int DeclarationRevision,
    int MaterialRevision, string DeclarationDigest, string MaterialDigest, string PolicyVersion, string PolicyDigest,
    string PersistedPolicyDigest, string ConfiguredPolicyDigest)
{
    public string Digest => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, JsonOptions))));
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true };
}
internal static class CoverageReviewState
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
    public static async Task<CoverageTuple?> ReadTuple(ApplicationDbContext db, AssetVersion version, AssetDraftWorkspace workspace, CancellationToken ct)
    {
        CodeAnalysisReportHeader? report = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleOrDefaultAsync(h => h.Id == version.SelectedCodeAnalysisReportId, ct);
        if (report?.AnalysisRunId is null)
        { return null; }
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == report.AnalysisRunId, ct);
        var policyDigest = await db.CodeAnalysisPolicyVersions.Where(p => p.Id == run.PolicyVersionId).Select(p => p.PolicyDigest).SingleAsync(ct);
        var declaration = await db.AssetSourceDeclarationRevisions.Where(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.SourceDeclarationHeadRevision).Select(r => r.ContentDigest).SingleOrDefaultAsync(ct);
        var material = await db.AssetMaterialMetadataRevisions.Where(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.MaterialMetadataHeadRevision).Select(r => r.ContentDigest).SingleOrDefaultAsync(ct);
        return declaration is null || material is null ? null : new(version.Id, version.ContentSha256, report.Id,
            workspace.SourceDeclarationHeadRevision, workspace.MaterialMetadataHeadRevision, declaration, material, report.PolicyVersion, run.PolicyDigest, policyDigest, ReviewAnalysisPolicy.DIGEST);
    }
    public static async Task<CoverageOwnerStatus> ReadStatus(ApplicationDbContext db, AssetVersion version, AssetDraftWorkspace workspace, CancellationToken ct)
    {
        CoverageTuple? tuple = await ReadTuple(db, version, workspace, ct);
        IQueryable<CoverageReviewRequest> requests = db.CoverageReviewRequests.AsNoTracking().Where(r => r.AssetVersionId == version.Id);
        CoverageReviewRequest? request = tuple is null ? null : await requests.Where(r => r.TupleDigest == tuple.Digest).OrderByDescending(r => r.RequestedWorkspaceRevision).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        request ??= await requests.OrderByDescending(r => r.RequestedWorkspaceRevision).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (request is null)
        { return new(null, "NOT_REQUESTED", null); }
        if (tuple?.Digest != request.TupleDigest)
        { return new(request.Id, "INVALIDATED", ErrorCodes.ERR_COVERAGE_ASSESSMENT_INVALIDATED); }
        CoverageAssessment? assessment = await db.CoverageAssessments.AsNoTracking().SingleOrDefaultAsync(a => a.RequestId == request.Id, ct);
        if (assessment is null)
        { return new(request.Id, "REQUESTED", ErrorCodes.ERR_REVIEW_COVERAGE_REQUIRED); }
        Guid ownerId = await db.Assets.Where(a => a.Id == version.AssetId).Select(a => a.AuthorId).SingleAsync(ct);
        var role = await db.Users.Where(u => u.Id == assessment.ModeratorUserId).Select(u => u.Role).SingleOrDefaultAsync(ct);
        if (!AppRoles.CanReview(role) || assessment.ModeratorUserId == ownerId || assessment.ModeratorUserId == request.OwnerUserId)
        { return new(request.Id, "INVALIDATED", ErrorCodes.ERR_COVERAGE_ASSESSOR_UNAVAILABLE); }
        return assessment.Outcome == CoverageAssessmentOutcome.ACCEPTED
            ? new(request.Id, "ACCEPTED", null) : new(request.Id, "BLOCKED", ErrorCodes.ERR_COVERAGE_ASSESSMENT_BLOCKED);
    }
    public static bool CanRequest(CoverageOwnerStatus status) => status.Status is "NOT_REQUESTED" or "INVALIDATED";
}
