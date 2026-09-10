using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBlock.SearchEvaluation.Validation;

public sealed record HumanQueryJudgmentDto(
    string DocumentKey,
    int Relevance);

public sealed record HumanQueryQrelsDto(
    string QueryId,
    List<HumanQueryJudgmentDto> Judgments);

public sealed record QrelsV1Dto(
    int Version,
    string Provenance,
    string AdjudicationDate,
    string AdjudicationVersion,
    List<HumanQueryQrelsDto> Queries);

public sealed record QrelsValidationResult(
    bool IsValid,
    List<string> Errors,
    QrelsV1Dto? Qrels);

public static class QrelsValidator
{
    private const string REQUIRED_PROVENANCE = "human-adjudicated";

    private static readonly JsonSerializerOptions _strictSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static string? ResolveSchemaPath(string qrelsFilePath)
    {
        var dir = Path.GetDirectoryName(qrelsFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "qrels.schema.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, "qrels.schema.json");
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var relativeCandidate = Path.Combine("asblock-backend", "search-evaluation", "qrels.schema.json");
        if (File.Exists(relativeCandidate))
        {
            return relativeCandidate;
        }

        var directCandidate = Path.Combine("search-evaluation", "qrels.schema.json");
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        var parentCandidate1 = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "search-evaluation", "qrels.schema.json");
        if (File.Exists(parentCandidate1))
        {
            return Path.GetFullPath(parentCandidate1);
        }

        var parentCandidate2 = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "asblock-backend", "search-evaluation", "qrels.schema.json");
        if (File.Exists(parentCandidate2))
        {
            return Path.GetFullPath(parentCandidate2);
        }

        return null;
    }

    public static QrelsValidationResult ValidateFile(string filePath, DatasetV1Dto? dataset = null)
    {
        if (!File.Exists(filePath))
        {
            return new QrelsValidationResult(false, [$"Qrels file not found at: {filePath}"], null);
        }

        var json = File.ReadAllText(filePath);
        var schemaPath = ResolveSchemaPath(filePath);
        return ValidateString(json, schemaPath, dataset);
    }

    private static QrelsValidationResult ValidateString(string json, string? schemaPath = null, DatasetV1Dto? dataset = null)
    {
        var errors = new List<string>();

        // 1. Strict schema checking against qrels.schema.json
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            errors.Add($"Failed to parse qrels JSON: {ex.Message}");
            return new QrelsValidationResult(false, errors, null);
        }

        using (jsonDoc)
        {
            if (string.IsNullOrEmpty(schemaPath) || !File.Exists(schemaPath))
            {
                schemaPath = ResolveSchemaPath(string.Empty);
            }

            if (string.IsNullOrEmpty(schemaPath) || !File.Exists(schemaPath))
            {
                errors.Add("qrels.schema.json not found for strict schema validation.");
            }
            else
            {
                try
                {
                    var schemaJson = File.ReadAllText(schemaPath);
                    using var schemaDoc = JsonDocument.Parse(schemaJson);
                    DatasetValidator.ValidateJsonAgainstSchema(jsonDoc.RootElement, schemaDoc.RootElement, "$", errors);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to execute JSON schema validation on qrels: {ex.Message}");
                }
            }
        }

        // 2. Strict deserialization (fails if reviewer identities or unmapped fields are present)
        QrelsV1Dto? qrels;
        try
        {
            qrels = JsonSerializer.Deserialize<QrelsV1Dto>(json, _strictSerializerOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"Strict JSON deserialization rejected qrels payload: {ex.Message}");
            return new QrelsValidationResult(false, errors, null);
        }

        if (qrels is null)
        {
            errors.Add("Deserialized qrels is null.");
            return new QrelsValidationResult(false, errors, null);
        }

        // 3. Provenance and structural invariants
        if (qrels.Version != 1)
        {
            errors.Add($"Expected qrels version 1, got {qrels.Version}.");
        }

        if (!string.Equals(qrels.Provenance, REQUIRED_PROVENANCE, StringComparison.Ordinal))
        {
            errors.Add($"Expected human-reviewed provenance '{REQUIRED_PROVENANCE}', got '{qrels.Provenance}'. Synthetic fixtures or unverified judgments cannot be used for release quality evaluation.");
        }

        if (string.IsNullOrWhiteSpace(qrels.AdjudicationVersion))
        {
            errors.Add("Adjudication version must be specified.");
        }

        if (string.IsNullOrWhiteSpace(qrels.AdjudicationDate))
        {
            errors.Add("Adjudication date must be specified.");
        }

        if (qrels.Queries.Count == 0)
        {
            errors.Add("Qrels must contain at least one adjudicated query.");
        }

        var validDocKeys = dataset?.Documents.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var seenQueryIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (HumanQueryQrelsDto q in qrels.Queries)
        {
            if (string.IsNullOrWhiteSpace(q.QueryId))
            {
                errors.Add("QueryId must not be empty.");
            }
            else if (!seenQueryIds.Add(q.QueryId))
            {
                errors.Add($"Duplicate queryId in qrels: '{q.QueryId}'.");
            }

            if (q.Judgments.Count == 0)
            {
                errors.Add($"Query '{q.QueryId}' has no judgments.");
            }
            else
            {
                var seenDocKeys = new HashSet<string>(StringComparer.Ordinal);
                var hasRelevant = false;

                foreach (HumanQueryJudgmentDto j in q.Judgments)
                {
                    if (string.IsNullOrWhiteSpace(j.DocumentKey))
                    {
                        errors.Add($"Query '{q.QueryId}' contains empty documentKey.");
                    }
                    else if (!seenDocKeys.Add(j.DocumentKey))
                    {
                        errors.Add($"Query '{q.QueryId}' contains duplicate judgment for document '{j.DocumentKey}'.");
                    }

                    if (validDocKeys != null && !validDocKeys.Contains(j.DocumentKey))
                    {
                        errors.Add($"Query '{q.QueryId}' references documentKey '{j.DocumentKey}' which does not exist in dataset.");
                    }

                    if (j.Relevance is < 0 or > 3)
                    {
                        errors.Add($"Query '{q.QueryId}' has invalid relevance grade {j.Relevance} (must be 0..3).");
                    }
                    if (j.Relevance >= 2)
                    {
                        hasRelevant = true;
                    }
                }

                if (!hasRelevant)
                {
                    errors.Add($"Query '{q.QueryId}' must have at least one judgment with relevance grade >= 2.");
                }
            }
        }

        if (dataset != null)
        {
            var datasetQueryIds = dataset.Queries.Select(dq => dq.Id).ToHashSet(StringComparer.Ordinal);

            var missingQueryIds = datasetQueryIds.Except(seenQueryIds).ToList();
            if (missingQueryIds.Count > 0)
            {
                errors.Add($"Qrels is incomplete: missing {missingQueryIds.Count} queries from dataset (e.g. {string.Join(", ", missingQueryIds.Take(5))}). Complete one-to-one coverage is required.");
            }

            var unknownQueryIds = seenQueryIds.Except(datasetQueryIds).ToList();
            if (unknownQueryIds.Count > 0)
            {
                errors.Add($"Qrels contains {unknownQueryIds.Count} unknown queries not in dataset (e.g. {string.Join(", ", unknownQueryIds.Take(5))}).");
            }
        }
        else
        {
            errors.Add("Dataset reference is required for one-to-one coverage validation of qrels.");
        }

        return new QrelsValidationResult(errors.Count == 0, errors, qrels);
    }
}
