using System.Text.Json;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Infrastructure.Persistence;

/// <summary>
/// The semantic contract an approved analysis policy manifest must satisfy before it can be
/// published or authorize a claim. It mirrors the frozen analysis service policy: the eight
/// mandatory stages, the language capabilities it can parse, the encoder representation retrieval
/// depends on, the immutable operation budget, the security validity window, and the pinned
/// reference/model/index identity. A manifest that only fills these columns with empty objects, or
/// that omits the pinned model, index or reference identity, is not an approved runtime manifest and
/// is refused rather than hashed and pinned.
/// </summary>
internal static class AnalysisManifestContract
{
    /// <summary>The mandatory stage matrix the frozen analysis policy declares, in execution order.</summary>
    private static readonly IReadOnlyList<string> _requiredStages =
    [
        "INVENTORY",
        "EXTRACTION",
        "TOKEN",
        "SEMANTIC",
        "LICENSE",
        "UNION",
        "VERIFIER",
        "REPORT"
    ];

    /// <summary>Language groups the frozen policy parses and pools for retrieval.</summary>
    private static readonly IReadOnlyList<string> _requiredLanguageGroups =
    [
        "javascript",
        "typescript",
        "python",
        "java",
        "csharp"
    ];

    private const int MAX_VERIFIER_REASON_LENGTH = 512;

    private static readonly IReadOnlyList<string> _digestToolIdentityMembers = ["checkpointSha256", "selectionSha256", "loadedStateSha256"];

    private static readonly IReadOnlyList<string> _textToolIdentityMembers = ["framing", "pooling", "mask", "preprocessing", "chunking"];

    private static readonly IReadOnlyList<string> _requiredSecurityValidityMembers = ["contentSha256", "scopeComplete", "scannerIdentity"];

    /// <summary>
    /// Returns why the manifest is not a publishable approved runtime manifest, or null when it is.
    /// Every failure names the missing or malformed member so a refused publication is actionable.
    /// </summary>
    public static string? Validate(AnalysisPolicyManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PolicyKey) || manifest.PolicyKey.Length > 64)
        {
            return "The policy key must be non-blank and at most 64 characters.";
        }

        if (!ProductDigests.IsLowercaseSha256Hex(manifest.PolicyDigest))
        {
            return "The policy digest must be 64 lowercase hexadecimal characters.";
        }

        if (string.IsNullOrWhiteSpace(manifest.SchemaName) || manifest.SchemaName.Length > 64)
        {
            return "The report contract name must be non-blank and at most 64 characters.";
        }

        if (string.IsNullOrWhiteSpace(manifest.VerifierMode) || manifest.VerifierMode.Length > 48)
        {
            return "The verifier mode must be non-blank and at most 48 characters.";
        }

        if (string.IsNullOrWhiteSpace(manifest.VerifierReason)
            || manifest.VerifierReason.Length > MAX_VERIFIER_REASON_LENGTH)
        {
            return $"The verifier reason must be non-blank and at most {MAX_VERIFIER_REASON_LENGTH} characters.";
        }

        return ValidateStageMatrix(manifest.StageMatrixJson)
            ?? ValidateToolIdentities(manifest.ToolIdentitiesJson)
            ?? ValidateLimits(manifest.LimitsJson)
            ?? ValidateSecurityValidity(manifest.SecurityValidityJson)
            ?? ValidatePinnedReference(manifest);
    }

    /// <summary>
    /// The stage matrix must declare exactly the frozen mandatory stages, in order, and the language
    /// capabilities the service can actually parse, so a manifest that quietly dropped a stage or a
    /// language cannot be approved.
    /// </summary>
    private static string? ValidateStageMatrix(string stageMatrixJson)
    {
        if (ParseObject(stageMatrixJson, "the stage matrix") is not { } root)
        {
            return "The stage matrix member must be a JSON object.";
        }

        if (root.TryGetProperty("stages", out JsonElement stages) && stages.ValueKind == JsonValueKind.Array)
        {
            var declared = new List<string>(stages.GetArrayLength());
            foreach (JsonElement stage in stages.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Object
                    || !TryReadString(stage, "stage", out var name)
                    || !TryReadString(stage, "applicability", out var applicability))
                {
                    return "Every declared stage must be an object with stage and applicability strings.";
                }

                if (!string.Equals(applicability, "MANDATORY", StringComparison.Ordinal))
                {
                    return $"Stage '{name}' must be MANDATORY in an approved runtime manifest.";
                }

                declared.Add(name);
            }

            if (!declared.SequenceEqual(_requiredStages, StringComparer.Ordinal))
            {
                return $"The stage matrix must declare exactly the mandatory stages {string.Join(", ", _requiredStages)}.";
            }
        }
        else
        {
            return "The stage matrix must declare a stages array.";
        }

        if (!root.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Array
            || capabilities.GetArrayLength() == 0)
        {
            return "The stage matrix must declare a non-empty capabilities array.";
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.Object
                || !TryReadString(capability, "group", out var group)
                || !capability.TryGetProperty("dialects", out JsonElement dialects)
                || dialects.ValueKind != JsonValueKind.Array
                || dialects.GetArrayLength() == 0)
            {
                return "Every capability must be an object with a group and a non-empty dialects array.";
            }

            foreach (JsonElement dialect in dialects.EnumerateArray())
            {
                if (dialect.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dialect.GetString()))
                {
                    return "Every declared dialect must be a non-blank string.";
                }
            }

            groups.Add(group);
        }

        foreach (var required in _requiredLanguageGroups)
        {
            if (!groups.Contains(required))
            {
                return $"The capability matrix must declare the '{required}' language group.";
            }
        }

        return null;
    }

    /// <summary>
    /// Tool identities must name a real frozen encoder: the checkpoint, tokenizer files and
    /// selection are digests, the representation members are non-blank, and the dimension is a
    /// positive number.
    /// </summary>
    private static string? ValidateToolIdentities(string toolIdentitiesJson)
    {
        if (ParseObject(toolIdentitiesJson, "the tool identity") is not { } root)
        {
            return "The tool identity member must be a JSON object.";
        }

        foreach (var digestMember in _digestToolIdentityMembers)
        {
            if (!TryReadString(root, digestMember, out var digest)
                || !ProductDigests.IsLowercaseSha256Hex(digest))
            {
                return $"The tool identity member '{digestMember}' must be 64 lowercase hexadecimal characters.";
            }
        }

        if (!root.TryGetProperty("tokenizerHashes", out JsonElement tokenizerHashes)
            || tokenizerHashes.ValueKind != JsonValueKind.Object
            || !tokenizerHashes.EnumerateObject().Any())
        {
            return "The tool identity must declare a non-empty tokenizerHashes object.";
        }

        foreach (JsonProperty file in tokenizerHashes.EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.String
                || !ProductDigests.IsLowercaseSha256Hex(file.Value.GetString() ?? ""))
            {
                return $"The tokenizer hash for '{file.Name}' must be 64 lowercase hexadecimal characters.";
            }
        }

        if (!root.TryGetProperty("parserBuilds", out JsonElement parserBuilds)
            || parserBuilds.ValueKind != JsonValueKind.Object
            || !parserBuilds.EnumerateObject().Any()
            || parserBuilds.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString())))
        {
            return "The tool identity must declare observed parser builds.";
        }

        foreach (var identity in new[] { "dolosVersion", "scancodeVersion", "serviceVersion", "referenceRevision" })
        {
            if (!TryReadString(root, identity, out var value) || string.IsNullOrWhiteSpace(value) || value.Length > 128)
            {
                return $"The tool identity member '{identity}' must be a bounded observed value.";
            }
        }

        foreach (var textMember in _textToolIdentityMembers)
        {
            if (!TryReadString(root, textMember, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return $"The tool identity member '{textMember}' must be a non-blank string.";
            }
        }

        if (!root.TryGetProperty("dimension", out JsonElement dimension)
            || dimension.ValueKind != JsonValueKind.Number
            || !dimension.TryGetInt32(out var size)
            || size <= 0)
        {
            return "The tool identity member 'dimension' must be a positive integer.";
        }

        return null;
    }

    /// <summary>The immutable per-analysis operation budget must be fully and positively specified.</summary>
    private static string? ValidateLimits(string limitsJson)
    {
        if (ParseObject(limitsJson, "the operation limits") is not { } root)
        {
            return "The operation limits member must be a JSON object.";
        }

        foreach (var member in _requiredLimitMembers)
        {
            if (!root.TryGetProperty(member, out JsonElement value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var number)
                || number <= 0)
            {
                return $"The operation limits member '{member}' must be a positive integer.";
            }
        }

        return null;
    }

    /// <summary>The security validity window must state what admitted proof has to satisfy.</summary>
    private static string? ValidateSecurityValidity(string securityValidityJson)
    {
        if (ParseObject(securityValidityJson, "the security validity") is not { } root)
        {
            return "The security validity member must be a JSON object.";
        }

        foreach (var required in _requiredSecurityValidityMembers)
        {
            if (!TryReadString(root, required, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return $"The security validity member '{required}' must be a non-blank string.";
            }
        }

        if (!root.TryGetProperty("signatureWindowDays", out JsonElement window)
            || window.ValueKind != JsonValueKind.Number
            || !window.TryGetInt64(out var days)
            || days <= 0)
        {
            return "The security validity member 'signatureWindowDays' must be a positive integer.";
        }

        return null;
    }

    /// <summary>
    /// Retrieval must be pinned to one reference snapshot built from one model and one index, so an
    /// approved manifest can never be published without the reference identity the reports rely on.
    /// </summary>
    private static string? ValidatePinnedReference(AnalysisPolicyManifest manifest)
    {
        if (manifest.ModelKey is null || !ProductDigests.IsLowercaseSha256Hex(manifest.ModelKey))
        {
            return "The pinned model key must be present as 64 lowercase hexadecimal characters.";
        }

        if (manifest.IndexKey is null || !ProductDigests.IsLowercaseSha256Hex(manifest.IndexKey))
        {
            return "The pinned reference index key must be present as 64 lowercase hexadecimal characters.";
        }

        if (string.IsNullOrWhiteSpace(manifest.ReferenceSnapshotId) || manifest.ReferenceSnapshotId.Length > 128)
        {
            return "The pinned reference snapshot id must be non-blank and at most 128 characters.";
        }

        return null;
    }

    private static readonly IReadOnlyList<string> _requiredLimitMembers =
    [
        "maxCompressedBytes",
        "maxEntries",
        "maxExpandedBytes",
        "maxSourceBytes",
        "maxUnits",
        "maxWindows",
        "maxResponseBytes",
        "maxFindings",
        "tokenK",
        "tokenWindow",
        "tokenTop",
        "semanticTop",
        "unionBudget"
    ];

    private static JsonElement? ParseObject(string json, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            _ = what;
            return null;
        }
    }

    private static bool TryReadString(JsonElement element, string member, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(member, out JsonElement found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString() ?? string.Empty;
        return true;
    }
}
