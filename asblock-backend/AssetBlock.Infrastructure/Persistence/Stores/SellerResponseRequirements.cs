using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed record SellerResponseRequirementState(bool Available, IReadOnlySet<Guid> Required,
    IReadOnlyDictionary<Guid, SellerResponse> Answers, SellerEvidenceSnapshot Snapshot)
{
    public int Outstanding => Required.Count(id => !Answers.ContainsKey(id));
}

/// <summary>Shared persisted response obligations; answers do not resolve findings.</summary>
internal static class SellerResponseRequirements
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<SellerResponseRequirementState> Read(ApplicationDbContext db, Guid reportId,
        AssetDraftWorkspace workspace, CancellationToken ct)
    {
        var empty = new SellerEvidenceSnapshot(reportId, [], "");
        CodeAnalysisReportHeader? report = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleOrDefaultAsync(h => h.Id == reportId, ct);
        List<CodeAnalysisReportFinding> findings = await db.CodeAnalysisReportFindings.AsNoTracking()
            .Where(f => f.ReportId == reportId).Take(SellerEvidenceLimits.MAX_RESPONSES + 1).ToListAsync(ct);
        var required = new HashSet<Guid>();
        if (report is null || report.FindingCount != findings.Count || findings.Count > SellerEvidenceLimits.MAX_RESPONSES)
        {
            return new(false, required, new Dictionary<Guid, SellerResponse>(), empty);
        }
        foreach (CodeAnalysisReportFinding finding in findings.Where(f => f.RetrievalEvidenceJson != null))
        {
            RetrievalCandidateEvidence evidence = JsonSerializer.Deserialize<RetrievalCandidateEvidence>(finding.RetrievalEvidenceJson!, RetrievalEvidenceValidator.JsonOptions)!;
            if (evidence.Material)
            { required.Add(finding.Id); }
        }
        SellerEvidenceSnapshot snapshot = empty;
        if (workspace.SellerEvidenceHeadRevision > 0)
        {
            var payload = await db.AssetSellerEvidenceRevisions.AsNoTracking().Where(e => e.WorkspaceId == workspace.Id
                && e.Revision == workspace.SellerEvidenceHeadRevision).Select(e => e.PayloadJson).SingleAsync(ct);
            snapshot = JsonSerializer.Deserialize<SellerEvidenceSnapshot>(payload, JsonOptions)!;
            if (snapshot.ReportId != reportId)
            { snapshot = empty with { Text = snapshot.Text }; }
        }
        if (snapshot.ResponseIds.Count > SellerEvidenceLimits.MAX_RESPONSES || snapshot.ResponseIds.Distinct().Count() != snapshot.ResponseIds.Count)
        {
            return new(false, required, new Dictionary<Guid, SellerResponse>(), snapshot);
        }
        List<SellerResponse> answers = await db.SellerResponses.AsNoTracking()
            .Where(r => snapshot.ResponseIds.Contains(r.Id) && r.WorkspaceId == workspace.Id
                && r.AssetVersionId == workspace.AssetVersionId && r.ReportId == reportId
                && r.SellerEvidenceRevision <= workspace.SellerEvidenceHeadRevision).ToListAsync(ct);
        var available = answers.Count == snapshot.ResponseIds.Count && answers.Select(r => r.FindingId).Distinct().Count() == answers.Count;
        return new(available, required, available ? answers.ToDictionary(r => r.FindingId) : new Dictionary<Guid, SellerResponse>(), snapshot);
    }
}
