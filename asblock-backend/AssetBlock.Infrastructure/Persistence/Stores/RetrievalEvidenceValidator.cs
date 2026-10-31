using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>Validates complete retrieval evidence without reconstructing absent channel values.</summary>
internal static class RetrievalEvidenceValidator
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static RetrievalCandidateEvidence? ParseCandidate(JsonElement finding, string type)
    {
        if (type is not ("SIMILARITY_TOKEN" or "SIMILARITY_SEMANTIC"))
        {
            Require(!finding.TryGetProperty("retrieval", out _));
            return null;
        }
        Require(finding.TryGetProperty("retrieval", out JsonElement detail));
        Exact(detail, "queryFileId", "queryUnit", "channels", "tokenRank", "tokenScore", "semanticRank", "semanticScore", "material");
        Require(detail.GetProperty("queryFileId").ValueKind == JsonValueKind.String
            && ProductDigests.IsLowercaseSha256Hex(detail.GetProperty("queryFileId").GetString() ?? "")
            && finding.GetProperty("fileId").GetString() == detail.GetProperty("queryFileId").GetString());
        Require(finding.TryGetProperty("foreign", out JsonElement foreign) && foreign.ValueKind == JsonValueKind.Object
            && foreign.TryGetProperty("fileId", out JsonElement foreignId) && foreignId.ValueKind == JsonValueKind.String
            && ProductDigests.IsLowercaseSha256Hex(foreignId.GetString() ?? ""));
        Require(detail.GetProperty("queryUnit").ValueKind == JsonValueKind.String && detail.GetProperty("queryUnit").GetString() == "FILE");
        Require(detail.GetProperty("channels").ValueKind == JsonValueKind.String);
        var channels = detail.GetProperty("channels").GetString();
        Require(channels is "TOKEN" or "SEMANTIC" or "TOKEN_AND_SEMANTIC");
        ValidateChannel(detail, "token", channels!.Contains("TOKEN", StringComparison.Ordinal), ReviewAnalysisPolicy.TOKEN_TOP);
        ValidateChannel(detail, "semantic", channels.Contains("SEMANTIC", StringComparison.Ordinal), ReviewAnalysisPolicy.SEMANTIC_TOP);
        Require(detail.GetProperty("material").ValueKind is JsonValueKind.True or JsonValueKind.False);
        RetrievalCandidateEvidence evidence = detail.Deserialize<RetrievalCandidateEvidence>(JsonOptions)!;
        Require(evidence.TokenRank.HasValue ? evidence.TokenRank != 1 || evidence.Material : !evidence.Material);
        Require(type == (evidence.TokenRank.HasValue ? "SIMILARITY_TOKEN" : "SIMILARITY_SEMANTIC"));
        return evidence;
    }

    public static RetrievalReportSummary ValidateSummary(JsonElement root, IReadOnlyList<ValidatedReportFinding> findings, int findingOverflow)
    {
        Require(root.TryGetProperty("retrievalSummary", out JsonElement summary));
        Exact(summary, "tokenTop", "semanticTop", "unionBudget", "queries");
        Require(ReadInt(summary, "tokenTop") == ReviewAnalysisPolicy.TOKEN_TOP
            && ReadInt(summary, "semanticTop") == ReviewAnalysisPolicy.SEMANTIC_TOP
            && ReadInt(summary, "unionBudget") == ReviewAnalysisPolicy.UNION_BUDGET);
        JsonElement queries = summary.GetProperty("queries");
        Require(queries.ValueKind == JsonValueKind.Array && queries.GetArrayLength() <= ReviewAnalysisPolicy.MAX_QUERIES);
        Require(queries.GetArrayLength() == root.GetProperty("coverage").GetProperty("analyzedFiles").GetInt32());
        var byQuery = new Dictionary<string, RetrievalQuerySummary>(StringComparer.Ordinal);
        foreach (JsonElement query in queries.EnumerateArray())
        {
            Exact(query, "queryFileId", "tokenMaxScore", "tokenCandidates", "tokenRetained", "tokenOverflow", "semanticCandidates", "semanticRetained", "semanticOverflow", "unionRetained", "reportOmitted", "reportOmittedToken", "reportOmittedSemantic");
            Require(query.GetProperty("queryFileId").ValueKind == JsonValueKind.String
                && ProductDigests.IsLowercaseSha256Hex(query.GetProperty("queryFileId").GetString() ?? ""));
            foreach (JsonProperty property in query.EnumerateObject().Where(p => p.Name is not ("queryFileId" or "tokenMaxScore")))
            {
                Require(ReadInt(query, property.Name) is >= 0 and <= 1000);
            }
            JsonElement maximum = query.GetProperty("tokenMaxScore");
            Require(maximum.ValueKind == JsonValueKind.Null || maximum.ValueKind == JsonValueKind.Number
                && maximum.TryGetDouble(out var score) && double.IsFinite(score) && score > 0 && score <= 1);
            RetrievalQuerySummary q = query.Deserialize<RetrievalQuerySummary>(JsonOptions)!;
            Require(byQuery.TryAdd(q.QueryFileId, q));
            Require(q.TokenRetained == Math.Min(q.TokenCandidates, ReviewAnalysisPolicy.TOKEN_TOP)
                && q.TokenOverflow == q.TokenCandidates - q.TokenRetained
                && q.SemanticRetained == Math.Min(q.SemanticCandidates, ReviewAnalysisPolicy.SEMANTIC_TOP)
                && q.SemanticOverflow == q.SemanticCandidates - q.SemanticRetained
                && q.UnionRetained >= Math.Max(q.TokenRetained, q.SemanticRetained)
                && q.UnionRetained <= Math.Min(ReviewAnalysisPolicy.UNION_BUDGET, q.TokenRetained + q.SemanticRetained)
                && q.ReportOmitted <= q.UnionRetained && q.ReportOmittedToken <= q.TokenRetained
                && q.ReportOmittedSemantic <= q.SemanticRetained
                && Math.Max(q.ReportOmittedToken, q.ReportOmittedSemantic) <= q.ReportOmitted
                && q.ReportOmitted <= q.ReportOmittedToken + q.ReportOmittedSemantic
                && (q.TokenRetained == 0 ? q.TokenMaxScore is null : q.TokenMaxScore is > 0 and <= 1));
        }
        var candidates = new Dictionary<string, List<(ValidatedReportFinding Finding, RetrievalCandidateEvidence Evidence)>>(StringComparer.Ordinal);
        foreach (ValidatedReportFinding finding in findings.Where(f => f.RetrievalEvidenceJson != null))
        {
            RetrievalCandidateEvidence evidence = JsonSerializer.Deserialize<RetrievalCandidateEvidence>(finding.RetrievalEvidenceJson!, JsonOptions)!;
            Require(byQuery.ContainsKey(evidence.QueryFileId));
            if (!candidates.TryGetValue(evidence.QueryFileId, out List<(ValidatedReportFinding Finding, RetrievalCandidateEvidence Evidence)>? rows))
            {
                rows = [];
                candidates.Add(evidence.QueryFileId, rows);
            }
            Require(!rows.Any(r => r.Finding.ForeignSourceId == finding.ForeignSourceId));
            rows.Add((finding, evidence));
        }
        foreach (RetrievalQuerySummary query in byQuery.Values)
        {
            List<(ValidatedReportFinding Finding, RetrievalCandidateEvidence Evidence)> rows = candidates.GetValueOrDefault(query.QueryFileId) ?? [];
            Require(rows.Count + query.ReportOmitted == query.UnionRetained);
            foreach ((ValidatedReportFinding Finding, RetrievalCandidateEvidence Evidence) row in rows)
            {
                Require(row.Evidence.TokenScore is null || row.Evidence.TokenScore <= query.TokenMaxScore);
                Require(row.Evidence.TokenRank != 1 || row.Evidence.TokenScore == query.TokenMaxScore);
                Require(row.Evidence.Material == (row.Evidence.TokenScore is > 0 && row.Evidence.TokenScore == query.TokenMaxScore));
            }
            ValidateRanks(rows.Where(r => r.Evidence.TokenRank.HasValue).Select(r => (r.Evidence.TokenRank!.Value, r.Evidence.TokenScore!.Value, r.Finding.ForeignSourceId!)).ToList(), query.TokenRetained, query.ReportOmittedToken, true);
            ValidateRanks(rows.Where(r => r.Evidence.SemanticRank.HasValue).Select(r => (r.Evidence.SemanticRank!.Value, r.Evidence.SemanticScore!.Value, r.Finding.ForeignSourceId!)).ToList(), query.SemanticRetained, query.ReportOmittedSemantic, false);
        }
        Require(byQuery.Values.Sum(q => q.ReportOmitted) <= findingOverflow);
        return summary.Deserialize<RetrievalReportSummary>(JsonOptions)!;
    }

    private static void ValidateRanks(List<(int Rank, double Score, string Id)> rows, int retained, int omitted, bool scoreTiesUseIdentity)
    {
        Require(rows.Count + omitted == retained && rows.Select(r => r.Rank).Distinct().Count() == rows.Count
            && rows.All(r => r.Rank <= retained));
        Require(rows.OrderBy(r => r.Rank).Select(r => r.Score).SequenceEqual(rows.OrderByDescending(r => r.Score).Select(r => r.Score)));
        // Equal clamped semantic scores do not prove equal original distances.
        if (scoreTiesUseIdentity)
        {
            Require(rows.OrderBy(r => r.Rank).Select(r => r.Id).SequenceEqual(
                rows.OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal).Select(r => r.Id)));
        }
    }

    private static void ValidateChannel(JsonElement detail, string channel, bool present, int top)
    {
        JsonElement rank = detail.GetProperty(channel + "Rank");
        JsonElement score = detail.GetProperty(channel + "Score");
        if (!present)
        {
            Require(rank.ValueKind == JsonValueKind.Null && score.ValueKind == JsonValueKind.Null);
            return;
        }
        Require(rank.ValueKind == JsonValueKind.Number && rank.TryGetInt32(out var r) && r >= 1 && r <= top);
        Require(score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var s) && double.IsFinite(s) && s >= 0 && s <= 1
            && (channel != "token" || s > 0));
    }

    private static int ReadInt(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        return value.GetInt32();
    }

    private static void Exact(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Count() == names.Length
            && element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names));
    }

    private static void Require(bool accepted)
    {
        if (!accepted)
        {
            throw new CodeAnalysisValidationException("Retrieval evidence contract is invalid.");
        }
    }
}
