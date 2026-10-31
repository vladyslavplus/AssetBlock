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
    internal sealed record SummaryInput(Guid Key, Guid ReportId, Guid WorkspaceId, Guid VersionId, int Revision);
    internal sealed record Summary(bool Available, IReadOnlySet<Guid> Required, IReadOnlySet<Guid> Answered)
    {
        public int Outstanding => Required.Count(id => !Answered.Contains(id));
    }
    private sealed class MaterialRow
    {
        public Guid ReportId { get; init; }
        public Guid FindingId { get; init; }
        public bool Material { get; init; }
    }
    private sealed class SnapshotRow
    {
        public Guid WorkspaceId { get; init; }
        public int Revision { get; init; }
        public string? ReportId { get; init; }
        public string? ResponseIds { get; init; }
    }

    internal static async Task<Dictionary<Guid, Summary>> ReadSummaries(ApplicationDbContext db, IReadOnlyList<SummaryInput> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0)
        { return []; }
        Guid[] reportIds = inputs.Select(i => i.ReportId).Distinct().ToArray();
        Guid[] workspaceIds = inputs.Select(i => i.WorkspaceId).ToArray();
        var revisions = inputs.Select(i => i.Revision).ToArray();
        var reports = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(r => reportIds.Contains(r.Id)).Select(r => new { r.Id, r.FindingCount }).ToDictionaryAsync(r => r.Id, ct);
        var limit = SellerEvidenceLimits.MAX_RESPONSES + 1;
        List<MaterialRow> findings = await db.Database.SqlQuery<MaterialRow>($"""
            SELECT "ReportId", "FindingId", "Material" FROM (
              SELECT "ReportId", "Id" AS "FindingId", COALESCE(("RetrievalEvidenceJson" ->> 'material')::boolean, false) AS "Material",
                     row_number() OVER (PARTITION BY "ReportId" ORDER BY "Id") AS ordinal
              FROM code_analysis_report_findings WHERE "ReportId" = ANY ({reportIds})
            ) AS bounded WHERE ordinal <= {limit}
            """).ToListAsync(ct);
        List<SnapshotRow> snapshots = await db.Database.SqlQuery<SnapshotRow>($"""
            SELECT e."WorkspaceId", e."Revision", e."PayloadJson" ->> 'reportId' AS "ReportId", (e."PayloadJson" -> 'responseIds')::text AS "ResponseIds"
            FROM asset_seller_evidence_revisions e
            JOIN unnest({workspaceIds}, {revisions}) AS selected(workspace_id, revision)
              ON e."WorkspaceId" = selected.workspace_id AND e."Revision" = selected.revision
            """).ToListAsync(ct);
        var memberships = new Dictionary<Guid, Guid[]>();
        foreach (SummaryInput input in inputs)
        {
            SnapshotRow? snapshot = input.Revision == 0 ? null : snapshots.SingleOrDefault(s => s.WorkspaceId == input.WorkspaceId && s.Revision == input.Revision);
            memberships[input.Key] = snapshot is not null && Guid.TryParse(snapshot.ReportId, out Guid snapshotReport) && snapshotReport == input.ReportId
                ? JsonSerializer.Deserialize<Guid[]>(snapshot.ResponseIds ?? "[]", JsonOptions) ?? [] : [];
        }
        Guid[] responseIds = memberships.Values.SelectMany(ids => ids).Distinct().ToArray();
        var answers = await db.SellerResponses.AsNoTracking().Where(r => responseIds.Contains(r.Id))
            .Select(r => new { r.Id, r.FindingId, r.ReportId, r.WorkspaceId, r.AssetVersionId, r.SellerEvidenceRevision }).ToListAsync(ct);
        var results = new Dictionary<Guid, Summary>();
        foreach (SummaryInput input in inputs)
        {
            MaterialRow[] reportFindings = findings.Where(f => f.ReportId == input.ReportId).ToArray();
            var required = reportFindings.Where(f => f.Material).Select(f => f.FindingId).ToHashSet();
            Guid[] ids = memberships[input.Key];
            var matching = answers.Where(a => ids.Contains(a.Id) && a.WorkspaceId == input.WorkspaceId && a.AssetVersionId == input.VersionId && a.ReportId == input.ReportId && a.SellerEvidenceRevision <= input.Revision).ToArray();
            var available = reports.TryGetValue(input.ReportId, out var report) && report.FindingCount == reportFindings.Length && reportFindings.Length <= SellerEvidenceLimits.MAX_RESPONSES
                && (input.Revision == 0 || snapshots.Any(s => s.WorkspaceId == input.WorkspaceId && s.Revision == input.Revision && Guid.TryParse(s.ReportId, out _) && s.ResponseIds is not null))
                && ids.Length <= SellerEvidenceLimits.MAX_RESPONSES && ids.Distinct().Count() == ids.Length
                && matching.Length == ids.Length && matching.Select(a => a.FindingId).Distinct().Count() == matching.Length;
            results[input.Key] = new(available, required, available ? matching.Select(a => a.FindingId).ToHashSet() : []);
        }
        return results;
    }
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<SellerResponseRequirementState> Read(ApplicationDbContext db, Guid reportId,
        AssetDraftWorkspace workspace, CancellationToken ct)
    {
        Summary summary = (await ReadSummaries(db, [new(workspace.Id, reportId, workspace.Id, workspace.AssetVersionId!.Value, workspace.SellerEvidenceHeadRevision)], ct))[workspace.Id];
        var empty = new SellerEvidenceSnapshot(reportId, [], "");
        if (!summary.Available)
        { return new(false, summary.Required, new Dictionary<Guid, SellerResponse>(), empty); }
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
            return new(false, summary.Required, new Dictionary<Guid, SellerResponse>(), snapshot);
        }
        List<SellerResponse> answers = await db.SellerResponses.AsNoTracking()
            .Where(r => snapshot.ResponseIds.Contains(r.Id) && r.WorkspaceId == workspace.Id
                && r.AssetVersionId == workspace.AssetVersionId && r.ReportId == reportId
                && r.SellerEvidenceRevision <= workspace.SellerEvidenceHeadRevision).ToListAsync(ct);
        return new(summary.Available, summary.Required, summary.Available ? answers.ToDictionary(r => r.FindingId) : new Dictionary<Guid, SellerResponse>(), snapshot);
    }
}
