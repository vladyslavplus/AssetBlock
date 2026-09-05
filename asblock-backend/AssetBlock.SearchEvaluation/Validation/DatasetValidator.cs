using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssetBlock.Application.Common;

namespace AssetBlock.SearchEvaluation.Validation;

public sealed record DatasetDocumentDto(
    string Key,
    string Title,
    string Description,
    string Category,
    List<string> Tags);

public sealed record QueryJudgmentDto(
    string DocumentKey,
    int Relevance);

public sealed record DatasetQueryDto(
    string Id,
    string Language,
    string Kind,
    string Text,
    List<QueryJudgmentDto> Judgments);

public sealed record DatasetV1Dto(
    int Version,
    string Provenance,
    List<DatasetDocumentDto> Documents,
    List<DatasetQueryDto> Queries);

public sealed record ValidationResult(
    bool IsValid,
    List<string> Errors,
    DatasetV1Dto? Dataset);

public static class DatasetValidator
{
    private const int MIN_DOCUMENTS = 60;
    private const int MIN_QUERIES = 90;
    private const int MIN_UKRAINIAN_QUERIES = 30;
    private const int MIN_ENGLISH_QUERIES = 30;
    private const int MIN_TECHNICAL_QUERIES = 30;
    private const int MIN_TYPO_QUERIES = 10;
    private const int MIN_CROSS_LANGUAGE_QUERIES = 10;

    private static readonly JsonSerializerOptions _strictSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static string? ResolveSchemaPath(string datasetFilePath)
    {
        var dir = Path.GetDirectoryName(datasetFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "dataset.schema.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, "dataset.schema.json");
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var relativeCandidate = Path.Combine("asblock-backend", "search-evaluation", "dataset.schema.json");
        if (File.Exists(relativeCandidate))
        {
            return relativeCandidate;
        }

        var directCandidate = Path.Combine("search-evaluation", "dataset.schema.json");
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        return null;
    }

    public static ValidationResult ValidateFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new ValidationResult(false, [$"Dataset file not found at: {filePath}"], null);
        }

        var json = File.ReadAllText(filePath);
        var schemaPath = ResolveSchemaPath(filePath);
        return ValidateString(json, schemaPath);
    }

    public static ValidationResult ValidateString(string json, string? schemaPath = null)
    {
        var errors = new List<string>();

        // 1. Strict JSON parsing and schema validation against dataset.schema.json
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            errors.Add($"Failed to parse dataset JSON: {ex.Message}");
            return new ValidationResult(false, errors, null);
        }

        using (jsonDoc)
        {
            if (string.IsNullOrEmpty(schemaPath) || !File.Exists(schemaPath))
            {
                schemaPath = ResolveSchemaPath(string.Empty);
            }

            if (string.IsNullOrEmpty(schemaPath) || !File.Exists(schemaPath))
            {
                errors.Add("dataset.schema.json not found for strict schema validation.");
            }
            else
            {
                try
                {
                    var schemaJson = File.ReadAllText(schemaPath);
                    using var schemaDoc = JsonDocument.Parse(schemaJson);
                    ValidateJsonAgainstSchema(jsonDoc.RootElement, schemaDoc.RootElement, "$", errors);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to execute JSON schema validation: {ex.Message}");
                }
            }
        }

        // 2. Strict deserialization (fails fast on unmapped / unknown fields)
        DatasetV1Dto? dataset;
        try
        {
            dataset = JsonSerializer.Deserialize<DatasetV1Dto>(json, _strictSerializerOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"Strict JSON deserialization rejected payload: {ex.Message}");
            return new ValidationResult(false, errors, null);
        }

        if (dataset is null)
        {
            errors.Add("Deserialized dataset is null.");
            return new ValidationResult(false, errors, null);
        }

        // 3. Domain invariant and dataset constraints
        if (dataset.Version != 1)
        {
            errors.Add($"Expected dataset version 1, got {dataset.Version}.");
        }

        if (!string.Equals(dataset.Provenance, "synthetic-and-reviewed", StringComparison.Ordinal))
        {
            errors.Add($"Expected provenance 'synthetic-and-reviewed', got '{dataset.Provenance}'.");
        }

        if (dataset.Documents.Count < MIN_DOCUMENTS)
        {
            errors.Add($"Expected at least {MIN_DOCUMENTS} documents, found {dataset.Documents.Count}.");
        }

        var docKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DatasetDocumentDto doc in dataset.Documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Key))
            {
                errors.Add("Document key must not be empty.");
            }
            else if (!docKeys.Add(doc.Key))
            {
                errors.Add($"Duplicate document key: '{doc.Key}'.");
            }

            if (string.IsNullOrWhiteSpace(doc.Title) || doc.Title.Length > 500)
            {
                errors.Add($"Document '{doc.Key}' has invalid title length (1..500).");
            }

            if (doc.Description is { Length: > 5000 })
            {
                errors.Add($"Document '{doc.Key}' has description exceeding 5000 characters.");
            }

            if (string.IsNullOrWhiteSpace(doc.Category) || doc.Category.Length > 200)
            {
                errors.Add($"Document '{doc.Key}' has invalid category length (1..200).");
            }

            if (doc.Tags.Count > 50)
            {
                errors.Add($"Document '{doc.Key}' has more than 50 tags.");
            }
            foreach (var tag in doc.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > 50)
                {
                    errors.Add($"Document '{doc.Key}' has tag '{tag}' exceeding 50 characters.");
                }
            }
        }

        if (dataset.Queries.Count < MIN_QUERIES)
        {
            errors.Add($"Expected at least {MIN_QUERIES} queries, found {dataset.Queries.Count}.");
        }

        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        var ukCount = 0;
        var enCount = 0;
        var techCount = 0;
        var typoCount = 0;
        var crossLangCount = 0;

        foreach (DatasetQueryDto q in dataset.Queries)
        {
            if (string.IsNullOrWhiteSpace(q.Id))
            {
                errors.Add("Query id must not be empty.");
            }
            else if (!queryIds.Add(q.Id))
            {
                errors.Add($"Duplicate query id: '{q.Id}'.");
            }

            // Strict query text scalar and control character validation
            if (!CatalogSearchNormalization.BeWithinUnicodeScalarLimit(q.Text))
            {
                errors.Add($"Query '{q.Id}' text exceeds maximum {CatalogSearchNormalization.MAX_UNICODE_SCALARS} Unicode scalars.");
            }

            if (!CatalogSearchNormalization.NotContainInvalidControlCharacters(q.Text))
            {
                errors.Add($"Query '{q.Id}' text contains invalid control characters.");
            }

            switch (q.Language.ToLowerInvariant())
            {
                case "uk":
                    ukCount++;
                    break;
                case "en":
                    enCount++;
                    break;
                case "technical":
                case "mixed":
                    techCount++;
                    break;
                default:
                    errors.Add($"Query '{q.Id}' has unknown language '{q.Language}'.");
                    break;
            }

            if (string.Equals(q.Kind, "typo", StringComparison.OrdinalIgnoreCase))
            {
                typoCount++;
            }
            else if (string.Equals(q.Kind, "cross-language", StringComparison.OrdinalIgnoreCase))
            {
                crossLangCount++;
            }

            if (q.Judgments.Count == 0)
            {
                errors.Add($"Query '{q.Id}' has no relevance judgments.");
            }
            else
            {
                var hasRelevant = false;
                var seenJudgmentDocKeys = new HashSet<string>(StringComparer.Ordinal);

                foreach (QueryJudgmentDto j in q.Judgments)
                {
                    if (!seenJudgmentDocKeys.Add(j.DocumentKey))
                    {
                        errors.Add($"Query '{q.Id}' contains duplicate judgment for documentKey '{j.DocumentKey}'.");
                    }

                    if (!docKeys.Contains(j.DocumentKey))
                    {
                        errors.Add($"Query '{q.Id}' references non-existent documentKey '{j.DocumentKey}'.");
                    }
                    if (j.Relevance is < 0 or > 3)
                    {
                        errors.Add($"Query '{q.Id}' has invalid relevance grade {j.Relevance} (must be 0..3).");
                    }
                    if (j.Relevance >= 2)
                    {
                        hasRelevant = true;
                    }
                }

                if (!hasRelevant)
                {
                    errors.Add($"Query '{q.Id}' must have at least one judgment with relevance grade >= 2.");
                }
            }
        }

        if (ukCount < MIN_UKRAINIAN_QUERIES)
        {
            errors.Add($"Expected at least {MIN_UKRAINIAN_QUERIES} Ukrainian queries, found {ukCount}.");
        }
        if (enCount < MIN_ENGLISH_QUERIES)
        {
            errors.Add($"Expected at least {MIN_ENGLISH_QUERIES} English queries, found {enCount}.");
        }
        if (techCount < MIN_TECHNICAL_QUERIES)
        {
            errors.Add($"Expected at least {MIN_TECHNICAL_QUERIES} Technical/mixed queries, found {techCount}.");
        }
        if (typoCount < MIN_TYPO_QUERIES)
        {
            errors.Add($"Expected at least {MIN_TYPO_QUERIES} typo queries, found {typoCount}.");
        }
        if (crossLangCount < MIN_CROSS_LANGUAGE_QUERIES)
        {
            errors.Add($"Expected at least {MIN_CROSS_LANGUAGE_QUERIES} cross-language queries, found {crossLangCount}.");
        }

        return new ValidationResult(errors.Count == 0, errors, dataset);
    }

    private static void ValidateJsonAgainstSchema(JsonElement element, JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // 1. Validate type
        if (schema.TryGetProperty("type", out JsonElement typeProp))
        {
            var expectedType = typeProp.GetString();
            switch (expectedType)
            {
                case "object":
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"{path}: expected JSON object, got {element.ValueKind}.");
                        return;
                    }
                    break;
                case "array":
                    if (element.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add($"{path}: expected JSON array, got {element.ValueKind}.");
                        return;
                    }
                    break;
                case "string":
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"{path}: expected JSON string, got {element.ValueKind}.");
                        return;
                    }
                    break;
                case "integer":
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out _))
                    {
                        errors.Add($"{path}: expected JSON integer, got {element.ValueKind}.");
                        return;
                    }
                    break;
            }
        }

        // 2. Validate const
        if (schema.TryGetProperty("const", out JsonElement constProp))
        {
            if (!JsonElement.DeepEquals(element, constProp))
            {
                errors.Add($"{path}: expected const '{constProp}', got '{element}'.");
            }
        }

        // 3. Validate enum
        if (schema.TryGetProperty("enum", out JsonElement enumProp) && enumProp.ValueKind == JsonValueKind.Array)
        {
            var matched = false;
            foreach (JsonElement allowed in enumProp.EnumerateArray())
            {
                if (JsonElement.DeepEquals(element, allowed))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                errors.Add($"{path}: value '{element}' is not in allowed enum values.");
            }
        }

        // 4. Object validations: required, additionalProperties, properties
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement requiredProp) && requiredProp.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement req in requiredProp.EnumerateArray())
                {
                    var reqName = req.GetString()!;
                    if (!element.TryGetProperty(reqName, out _))
                    {
                        errors.Add($"{path}: missing required property '{reqName}'.");
                    }
                }
            }

            var hasProperties = schema.TryGetProperty("properties", out JsonElement propertiesProp);
            var disallowAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additionalProp)
                && additionalProp.ValueKind == JsonValueKind.False;

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (hasProperties && propertiesProp.TryGetProperty(prop.Name, out JsonElement childSchema))
                {
                    ValidateJsonAgainstSchema(prop.Value, childSchema, $"{path}.{prop.Name}", errors);
                }
                else if (disallowAdditional)
                {
                    errors.Add($"{path}: unknown property '{prop.Name}' is not permitted by schema.");
                }
            }
        }

        // 5. Array validations: minItems, maxItems, items
        if (element.ValueKind == JsonValueKind.Array)
        {
            var count = element.GetArrayLength();
            if (schema.TryGetProperty("minItems", out JsonElement minItems) && count < minItems.GetInt32())
            {
                errors.Add($"{path}: array length {count} is less than minItems {minItems.GetInt32()}.");
            }
            if (schema.TryGetProperty("maxItems", out JsonElement maxItems) && count > maxItems.GetInt32())
            {
                errors.Add($"{path}: array length {count} exceeds maxItems {maxItems.GetInt32()}.");
            }
            if (schema.TryGetProperty("items", out JsonElement itemsSchema))
            {
                var idx = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateJsonAgainstSchema(item, itemsSchema, $"{path}[{idx}]", errors);
                    idx++;
                }
            }
        }

        // 6. String validations: minLength, maxLength, pattern
        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString()!;
            if (schema.TryGetProperty("minLength", out JsonElement minLen) && str.Length < minLen.GetInt32())
            {
                errors.Add($"{path}: string length {str.Length} is less than minLength {minLen.GetInt32()}.");
            }
            if (schema.TryGetProperty("maxLength", out JsonElement maxLen) && str.Length > maxLen.GetInt32())
            {
                errors.Add($"{path}: string length {str.Length} exceeds maxLength {maxLen.GetInt32()}.");
            }
            if (schema.TryGetProperty("pattern", out JsonElement patternProp))
            {
                var pattern = patternProp.GetString()!;
                if (!Regex.IsMatch(str, pattern))
                {
                    errors.Add($"{path}: string '{str}' does not match pattern '{pattern}'.");
                }
            }
        }

        // 7. Number validations: minimum, maximum
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var num))
        {
            if (schema.TryGetProperty("minimum", out JsonElement minProp) && num < minProp.GetInt64())
            {
                errors.Add($"{path}: value {num} is less than minimum {minProp.GetInt64()}.");
            }
            if (schema.TryGetProperty("maximum", out JsonElement maxProp) && num > maxProp.GetInt64())
            {
                errors.Add($"{path}: value {num} exceeds maximum {maxProp.GetInt64()}.");
            }
        }
    }
}
