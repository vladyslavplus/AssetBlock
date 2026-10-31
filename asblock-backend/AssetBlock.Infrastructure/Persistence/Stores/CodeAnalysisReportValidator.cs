using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Strict bounded validator for COMPLETED analysis envelopes. Every binding is checked
/// against the persisted run and pinned policy; fabricated authority, missing or duplicate
/// stages, coverage inconsistencies and digest mismatches fail closed.
/// </summary>
internal sealed class CodeAnalysisReportValidator : ICodeAnalysisReportValidator
{
    /// <summary>An operation timeout bounds every stage, so no stage may report a longer one.</summary>
    private const double MAX_STAGE_DURATION_SECONDS = 3600d;

    private static readonly HashSet<string> _findingTypes = new(StringComparer.Ordinal)
    {
        "SIMILARITY_TOKEN", "SIMILARITY_SEMANTIC", "LICENSE_UNKNOWN", "LICENSE_CONTRADICTION", "NOTICE_MISSING"
    };

    public ValidatedCodeAnalysisReport ValidateCompleted(
        string responseBody,
        CodeAnalysisRun run,
        CodeAnalysisPolicyVersion policy,
        CodeAnalysisReuseSourceBinding? expectedReuseSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseBody);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            throw new CodeAnalysisValidationException("Response is not valid JSON.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Response must be a JSON object.");
            }

            RequireString(root, "schema", "code-analysis-v1");
            RequireString(root, "outcome", "COMPLETED");
            RequireString(root, "serviceVersion", ApprovedManifest.Read(policy).ServiceVersion);
            RequireString(root, "runId", run.Id.ToString("D"), ordinalIgnoreCase: true);
            RequireString(root, "inputDigest", run.InputDigest);
            RequireString(root, "contentSha256", run.ContentSha256);
            RequireString(root, "assetId", run.AssetId.ToString("D"), ordinalIgnoreCase: true);
            RequireString(root, "versionId", run.AssetVersionId.ToString("D"), ordinalIgnoreCase: true);

            // Fabricated authority is rejected: the service must never admit.
            if (root.TryGetProperty("admittedBy", out JsonElement admittedBy)
                && admittedBy.ValueKind != JsonValueKind.Null)
            {
                throw new CodeAnalysisValidationException("Fabricated admission authority.");
            }

            // Cleanup must be confirmed clean: any non-null value blocks admission.
            if (root.TryGetProperty("cleanupError", out JsonElement cleanup)
                && cleanup.ValueKind != JsonValueKind.Null
                && cleanup.ValueKind != JsonValueKind.Undefined)
            {
                throw new CodeAnalysisValidationException("Unconfirmed containment cleanup.");
            }

            // Exact policy binding: key and digest are distinct identities.
            if (!root.TryGetProperty("policy", out JsonElement policyJson)
                || policyJson.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing policy binding.");
            }

            RequireString(policyJson, "policyVersion", policy.PolicyKey);
            RequireString(policyJson, "policyDigest", policy.PolicyDigest);
            if (policy.PolicyKey != ReviewAnalysisPolicy.KEY || policy.PolicyDigest != ReviewAnalysisPolicy.DIGEST
                || policy.VerifierMode != ReviewAnalysisPolicy.VERIFIER_MODE
                || policy.VerifierReason != ReviewAnalysisPolicy.VERIFIER_REASON)
            {
                throw new CodeAnalysisValidationException("Unrecognized review policy identity.");
            }
            RequireString(policyJson, "verifierMode", ReviewAnalysisPolicy.VERIFIER_MODE);
            RequireString(policyJson, "evidenceSha256", ReviewAnalysisPolicy.EVIDENCE_SHA256);
            if ((root.TryGetProperty("purpose", out JsonElement purpose)
                    && (purpose.ValueKind != JsonValueKind.String || purpose.GetString() != "PRODUCTION"))
                || root.TryGetProperty("simulated", out _) || root.TryGetProperty("fixture", out _))
            {
                throw new CodeAnalysisValidationException("Non-production envelope cannot be admitted.");
            }
            if (root.TryGetProperty("reviewEligible", out _) || policyJson.TryGetProperty("reviewEligible", out _))
            {
                throw new CodeAnalysisValidationException("The producer cannot assert review eligibility.");
            }

            if (!string.Equals(run.PolicyDigest, policy.PolicyDigest, StringComparison.Ordinal))
            {
                throw new CodeAnalysisValidationException("Run policy digest mismatch.");
            }

            // Exact security evidence binding.
            if (!root.TryGetProperty("securityEvidence", out JsonElement evidence)
                || evidence.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing security evidence binding.");
            }

            RequireString(evidence, "contentSha256", run.ContentSha256);
            RequireGuid(evidence, "archiveEvidenceId", run.ArchiveEvidenceId);
            RequireGuid(evidence, "malwareEvidenceId", run.MalwareEvidenceId);
            RequireInt(evidence, "securityGeneration", run.SecurityGeneration);

            // Exact reservation binding: service key and generation must match the run.
            if (!root.TryGetProperty("reservation", out JsonElement reservation)
                || reservation.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing reservation binding.");
            }

            RequireString(reservation, "serviceKey", run.ServiceKey);
            if (run.ReservationGeneration is not { } generation || generation < 1)
            {
                throw new CodeAnalysisValidationException("Run has no reservation generation.");
            }

            RequireInt(reservation, "generation", generation);

            // Stages: exactly the 8 expected mandatory stages, no missing or duplicate.
            if (!root.TryGetProperty("stages", out JsonElement stages)
                || stages.ValueKind != JsonValueKind.Array)
            {
                throw new CodeAnalysisValidationException("Missing stages.");
            }

            List<ValidatedReportStage> parsedStages = ParseStages(stages);
            IReadOnlyList<ValidatedAnalysisReuseArtifact> reuseArtifacts = ParseReuseArtifacts(
                root, run, policy, parsedStages, expectedReuseSource);

            // Coverage / findings / omissions consistency.
            if (!root.TryGetProperty("coverage", out JsonElement coverage)
                || coverage.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing coverage.");
            }

            var level = RequireString(coverage, "level", null);
            if (level != "FULL" && level != "PARTIAL" && level != "UNSUPPORTED")
            {
                throw new CodeAnalysisValidationException("Unknown coverage level.");
            }

            var analyzedFiles = RequireNonNegativeInt(coverage, "analyzedFiles");
            var omissionOverflow = RequireNonNegativeInt(coverage, "omissionOverflow", allowMissing: true);
            var omissions = 0;
            if (coverage.TryGetProperty("omissions", out JsonElement omissionsJson))
            {
                if (omissionsJson.ValueKind != JsonValueKind.Array)
                {
                    throw new CodeAnalysisValidationException("Invalid omissions.");
                }

                omissions = omissionsJson.GetArrayLength();
                if (omissions > 1000)
                {
                    throw new CodeAnalysisValidationException("Omission budget exceeded.");
                }
            }

            if (level == "FULL" && (omissions != 0 || omissionOverflow != 0))
            {
                throw new CodeAnalysisValidationException("FULL coverage must have no omissions.");
            }

            if (level == "UNSUPPORTED" && analyzedFiles != 0)
            {
                throw new CodeAnalysisValidationException("UNSUPPORTED coverage must analyze no files.");
            }

            if (level == "PARTIAL" && omissions == 0 && omissionOverflow == 0)
            {
                throw new CodeAnalysisValidationException("PARTIAL coverage must list omissions.");
            }

            if (!root.TryGetProperty("findings", out JsonElement findingsJson)
                || findingsJson.ValueKind != JsonValueKind.Array)
            {
                throw new CodeAnalysisValidationException("Missing findings.");
            }

            if (findingsJson.GetArrayLength() > 200)
            {
                throw new CodeAnalysisValidationException("Finding budget exceeded.");
            }

            List<ValidatedReportFinding> parsedFindings = ParseFindings(findingsJson);
            var findingOverflow = RequireNonNegativeInt(root, "findingOverflow", allowMissing: true);
            ValidateReportStageCounts(parsedStages, analyzedFiles, omissions, parsedFindings.Count, findingOverflow, omissionOverflow);

            // Report digest over the pre-REPORT payload: recompute reportId from the body
            // without reportId, sellerSafe and without the REPORT stage record.
            var reportId = RequireSha(root, "reportId");
            var recomputed = RecomputeReportId(root);
            if (!string.Equals(reportId, recomputed, StringComparison.Ordinal))
            {
                throw new CodeAnalysisValidationException("Report digest mismatch.");
            }

            // Stage provenance is bound to the approved manifest this deployment pinned, and the
            // REPORT stage has to record the exact digest of its own input and its report identity.
            ValidateStageProvenance(root, policy, reportId, parsedStages);

            // Server recomputed digest over the full admitted body.
            var reportDigest = CanonicalJson.Sha256Hex(root);

            // The seller-safe projection is mandatory on a completed report. It has to bind the same
            // report and run identity, agree with the report's own counts, carry no publication
            // authority, and stay inside the owner-safe field set: foreign identity, attribution
            // detail, scores and vectors never appear in it.
            if (!root.TryGetProperty("sellerSafe", out JsonElement sellerSafe)
                || sellerSafe.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing seller-safe projection.");
            }

            RequireString(sellerSafe, "reportId", reportId);
            RequireString(sellerSafe, "runId", run.Id.ToString("D"), ordinalIgnoreCase: true);
            RequireString(sellerSafe, "outcome", "COMPLETED");
            RequireString(sellerSafe, "coverage", level);

            if (sellerSafe.TryGetProperty("reviewEligible", out _))
            {
                throw new CodeAnalysisValidationException("The producer cannot assert review eligibility.");
            }

            if (RequireNonNegativeInt(sellerSafe, "omissionCount") != omissions)
            {
                throw new CodeAnalysisValidationException("Seller projection omission count mismatch.");
            }

            if (RequireNonNegativeInt(sellerSafe, "findingOverflow") != findingOverflow)
            {
                throw new CodeAnalysisValidationException("Seller projection overflow mismatch.");
            }

            ValidateSellerSafeFindings(sellerSafe, parsedFindings);

            var snapshotId = "";
            var revision = "";
            if (root.TryGetProperty("comparedScope", out JsonElement scope)
                && scope.ValueKind == JsonValueKind.Object)
            {
                snapshotId = scope.TryGetProperty("snapshotId", out JsonElement snap) && snap.ValueKind == JsonValueKind.String
                    ? snap.GetString() ?? "" : "";
                revision = scope.TryGetProperty("revision", out JsonElement rev) && rev.ValueKind == JsonValueKind.String
                    ? rev.GetString() ?? "" : "";
            }

            return new ValidatedCodeAnalysisReport(
                reportId,
                reportDigest,
                level,
                analyzedFiles,
                omissions,
                parsedFindings.Count,
                parsedStages,
                parsedFindings,
                snapshotId,
                revision,
                reuseArtifacts);
        }
    }

    /// <summary>
    /// Binds what each stage claims to have run to the approved manifest this deployment pinned, so a
    /// self-consistent report still cannot be admitted while describing a different encoder,
    /// tokenizer, tool build, reference snapshot or verification mode than the one approved.
    /// <para>
    /// The semantic stage must name the approved encoder, its pinned model and index, the approved
    /// checkpoint, framing and pooling; the token stage must use the approved k and window and name
    /// the observed tool build; the licence stage must name the observed scanner build; the
    /// extraction stage must report its observed parsers; the verifier stage must report the approved
    /// verifier mode; the compared scope must be the approved reference snapshot; and the REPORT
    /// stage must record the digest of its exact input and its report identity.
    /// </para>
    /// </summary>
    private static void ValidateStageProvenance(
        JsonElement root,
        CodeAnalysisPolicyVersion policy,
        string reportId,
        List<ValidatedReportStage> stages)
    {
        var manifest = ApprovedManifest.Read(policy);

        foreach (ValidatedReportStage stage in stages)
        {
            using var tool = JsonDocument.Parse(stage.ToolIdentityJson);
            if (tool.RootElement.TryGetProperty("fixture", out _) || tool.RootElement.TryGetProperty("simulated", out _))
            {
                throw new CodeAnalysisValidationException("Simulated stage evidence cannot be admitted.");
            }

            switch (stage.Stage)
            {
                case "SEMANTIC":
                    RequireToolMember(tool.RootElement, "encoder", manifest.Encoder);
                    RequireToolMember(tool.RootElement, "modelKey", manifest.ModelKey);
                    RequireToolMember(tool.RootElement, "indexKey", manifest.IndexKey);
                    RequireToolMember(tool.RootElement, "loadedCheckpointSha256", manifest.CheckpointSha256);
                    RequireToolMember(tool.RootElement, "loadedStateSha256", manifest.LoadedStateSha256);
                    RequireToolMember(tool.RootElement, "framing", manifest.Framing);
                    RequireToolMember(tool.RootElement, "pooling", manifest.Pooling);
                    RequireToolMember(tool.RootElement, "mask", manifest.Mask);
                    RequireToolMember(tool.RootElement, "preprocessing", manifest.Preprocessing);
                    RequireToolMember(tool.RootElement, "chunking", manifest.Chunking);
                    RequireToolNumber(tool.RootElement, "dimension", manifest.Dimension);
                    RequireJsonObjectDigest(tool.RootElement, "tokenizerHashes", manifest.TokenizerHashesDigest);
                    RequireToolMember(tool.RootElement, "selectionSha256", manifest.SelectionSha256);
                    RequireNonBlankToolMember(tool.RootElement, "device");
                    if (!tool.RootElement.TryGetProperty("device", out JsonElement devEl)
                        || devEl.ValueKind != JsonValueKind.String
                        || !string.Equals(devEl.GetString(), "cpu", StringComparison.Ordinal))
                    {
                        throw new CodeAnalysisValidationException("Stage tool identity 'device' must be \"cpu\".");
                    }
                    if (!tool.RootElement.TryGetProperty("loadedStateSha256", out JsonElement lss)
                        || lss.ValueKind != JsonValueKind.String
                        || !ProductDigests.IsLowercaseSha256Hex(lss.GetString() ?? ""))
                    {
                        throw new CodeAnalysisValidationException("Stage tool identity 'loadedStateSha256' is missing or invalid.");
                    }
                    break;

                case "TOKEN":
                    RequireToolMember(tool.RootElement, "name", "dolos");
                    RequireToolNumber(tool.RootElement, "k", manifest.TokenK);
                    RequireToolNumber(tool.RootElement, "window", manifest.TokenWindow);
                    RequireToolMember(tool.RootElement, "version", manifest.DolosVersion);
                    break;

                case "LICENSE":
                    RequireToolMember(tool.RootElement, "name", "scancode");
                    RequireToolMember(tool.RootElement, "version", manifest.ScanCodeVersion);
                    break;

                case "EXTRACTION":
                    if (!tool.RootElement.TryGetProperty("parsers", out JsonElement parsers)
                        || parsers.ValueKind != JsonValueKind.Object
                        || !parsers.EnumerateObject().Any()
                        || !string.Equals(CanonicalJson.Sha256Hex(parsers), manifest.ParserBuildsDigest, StringComparison.Ordinal))
                    {
                        throw new CodeAnalysisValidationException("Extraction must report its observed parsers.");
                    }

                    break;

                case "VERIFIER":
                    RequireToolMember(tool.RootElement, "mode", policy.VerifierMode);
                    RequireToolMember(tool.RootElement, "reason", ReviewAnalysisPolicy.VERIFIER_REASON);
                    RequireToolMember(tool.RootElement, "evidenceSha256", ReviewAnalysisPolicy.EVIDENCE_SHA256);
                    break;

                case "REPORT":
                    if (!string.Equals(stage.OutputSha256, reportId, StringComparison.Ordinal))
                    {
                        throw new CodeAnalysisValidationException("The report stage must output the report identity.");
                    }

                    // The report stage digests exactly the finding keys, coverage block and stage
                    // names it summarizes; anything else is not the report's real input.
                    var reportInput = new JsonObject
                    {
                        ["findings"] = new JsonArray(
                            root.GetProperty("findings").EnumerateArray()
                                .Select(f => (JsonNode)f.GetProperty("findingId").GetString()!)
                                .ToArray()),
                        ["coverage"] = JsonNode.Parse(root.GetProperty("coverage").GetRawText())!,
                        ["stages"] = new JsonArray(
                            stages.Where(s => s.Stage != "REPORT")
                                .Select(s => (JsonNode)s.Stage)
                                .ToArray())
                    };
                    using (var reportInputDocument = JsonDocument.Parse(reportInput.ToJsonString()))
                    {
                        if (!string.Equals(
                                stage.InputSha256,
                                CanonicalJson.Sha256Hex(reportInputDocument.RootElement),
                                StringComparison.Ordinal))
                        {
                            throw new CodeAnalysisValidationException(
                                "The report stage input digest does not match its input.");
                        }
                    }

                    break;
            }
        }

        // The compared scope is the approved reference snapshot, never a foreign one.
        if (manifest.ReferenceSnapshotId.Length > 0)
        {
            if (!root.TryGetProperty("comparedScope", out JsonElement scope) || scope.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Missing compared scope.");
            }

            RequireString(scope, "snapshotId", manifest.ReferenceSnapshotId);
            RequireString(scope, "revision", manifest.ReferenceRevision);
        }
    }

    private static void RequireToolMember(JsonElement tool, string member, string expected)
    {
        if (!TryReadToolString(tool, member, out var actual)
            || !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new CodeAnalysisValidationException(
                $"Stage tool identity '{member}' is not the approved identity.");
        }
    }

    private static void RequireNonBlankToolMember(JsonElement tool, string member)
    {
        if (!TryReadToolString(tool, member, out var actual) || string.IsNullOrWhiteSpace(actual))
        {
            throw new CodeAnalysisValidationException($"Stage tool identity '{member}' is missing.");
        }
    }

    private static void RequireToolNumber(JsonElement tool, string member, long expected)
    {
        if (!tool.TryGetProperty(member, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var actual)
            || actual != expected)
        {
            throw new CodeAnalysisValidationException(
                $"Stage tool identity '{member}' is not the approved value.");
        }
    }

    private static void RequireJsonObjectDigest(JsonElement element, string member, string expectedDigest)
    {
        if (!element.TryGetProperty(member, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object
            || !string.Equals(CanonicalJson.Sha256Hex(value), expectedDigest, StringComparison.Ordinal))
        {
            throw new CodeAnalysisValidationException($"Stage tool identity '{member}' is not the approved identity.");
        }
    }

    private static bool TryReadToolString(JsonElement tool, string member, out string? value)
    {
        value = null;
        if (!tool.TryGetProperty(member, out JsonElement found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString();
        return true;
    }

    /// <summary>
    /// The approved runtime manifest as the persisted policy row states it. Fixed-width columns come
    /// back space-padded, so every pinned identity is trimmed before it is compared.
    /// </summary>
    private sealed record ApprovedManifest(
        string Encoder,
        string ModelKey,
        string IndexKey,
        string CheckpointSha256,
        string LoadedStateSha256,
        string TokenizerHashesDigest,
        string SelectionSha256,
        string Framing,
        string Pooling,
        string Mask,
        string Preprocessing,
        string Chunking,
        long Dimension,
        string DolosVersion,
        string ScanCodeVersion,
        string ServiceVersion,
        string ReferenceRevision,
        string ParserBuildsDigest,
        long TokenK,
        long TokenWindow,
        string ReferenceSnapshotId)
    {
        public static ApprovedManifest Read(CodeAnalysisPolicyVersion policy) =>
            new(ReadString(policy.ToolIdentitiesJson, "encoder"),
                (policy.ModelKey ?? "").Trim(),
                (policy.IndexKey ?? "").Trim(),
                ReadString(policy.ToolIdentitiesJson, "checkpointSha256"),
                ReadString(policy.ToolIdentitiesJson, "loadedStateSha256"),
                ReadObjectDigest(policy.ToolIdentitiesJson, "tokenizerHashes"),
                ReadString(policy.ToolIdentitiesJson, "selectionSha256"),
                ReadString(policy.ToolIdentitiesJson, "framing"),
                ReadString(policy.ToolIdentitiesJson, "pooling"),
                ReadString(policy.ToolIdentitiesJson, "mask"),
                ReadString(policy.ToolIdentitiesJson, "preprocessing"),
                ReadString(policy.ToolIdentitiesJson, "chunking"),
                ReadNumber(policy.ToolIdentitiesJson, "dimension"),
                ReadString(policy.ToolIdentitiesJson, "dolosVersion"),
                ReadString(policy.ToolIdentitiesJson, "scancodeVersion"),
                ReadString(policy.ToolIdentitiesJson, "serviceVersion"),
                ReadString(policy.ToolIdentitiesJson, "referenceRevision"),
                ReadObjectDigest(policy.ToolIdentitiesJson, "parserBuilds"),
                ReadNumber(policy.LimitsJson, "tokenK"),
                ReadNumber(policy.LimitsJson, "tokenWindow"),
                (policy.ReferenceSnapshotId ?? "").Trim());

        private static string ReadString(string json, string member)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(member, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? (value.GetString() ?? "").Trim()
                    : "";
        }

        private static long ReadNumber(string json, string member)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(member, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var parsed)
                    ? parsed
                    : 0;
        }

        private static string ReadObjectDigest(string json, string member)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(member, out JsonElement value)
                && value.ValueKind == JsonValueKind.Object
                    ? CanonicalJson.Sha256Hex(value)
                    : "";
        }
    }

    private static IReadOnlyList<ValidatedAnalysisReuseArtifact> ParseReuseArtifacts(
        JsonElement root, CodeAnalysisRun run, CodeAnalysisPolicyVersion policy,
        IReadOnlyList<ValidatedReportStage> stages, CodeAnalysisReuseSourceBinding? expectedReuseSource)
    {
        ValidatedReportStage[] reusedStages = stages.Where(s => s.ReuseParentRunId is not null || s.ReuseParentStageId is not null).ToArray();
        if (!root.TryGetProperty("reuseArtifacts", out JsonElement artifacts))
        {
            if (reusedStages.Length > 0 || expectedReuseSource is not null)
            {
                throw new CodeAnalysisValidationException("Reuse provenance has no persisted artifacts.");
            }

            return [];
        }

        string[] expected = ["EXTRACTION", "TOKEN", "SEMANTIC"];
        if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() != expected.Length)
        {
            throw new CodeAnalysisValidationException("Reuse artifacts must contain extraction, token and semantic stages.");
        }

        var result = new List<ValidatedAnalysisReuseArtifact>(expected.Length);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (JsonElement item in artifacts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !HasExactProperties(item, "stage", "artifactDigest", "artifactJson")
                || !TryReadString(item, "stage", out var stage)
                || !expected.Contains(stage!, StringComparer.Ordinal)
                || !TryReadString(item, "artifactDigest", out var digest)
                || !ProductDigests.IsLowercaseSha256Hex(digest)
                || !TryReadString(item, "artifactJson", out var artifactJson))
            {
                throw new CodeAnalysisValidationException("Reuse artifact envelope is malformed.");
            }

            using var artifactDocument = JsonDocument.Parse(artifactJson!);
            JsonElement artifact = artifactDocument.RootElement;
            if (artifact.ValueKind != JsonValueKind.Object
                || !HasExactProperties(artifact, "binding", "data")
                || !artifact.TryGetProperty("binding", out JsonElement binding)
                || !HasReuseBinding(binding, stage!, run, policy)
                || !artifact.TryGetProperty("data", out JsonElement data)
                || !ValidateReuseData(stage!, data))
            {
                throw new CodeAnalysisValidationException($"Reuse artifact '{stage}' is invalid or stale.");
            }

            var canonical = CanonicalJson.Canonicalize(artifact);
            if (!string.Equals(CanonicalJson.Sha256Hex(artifact), digest, StringComparison.Ordinal)
                || canonical.Length > 262144
                || !digests.TryAdd(stage!, digest!))
            {
                throw new CodeAnalysisValidationException($"Reuse artifact '{stage}' digest or size mismatch.");
            }

            totalBytes += canonical.Length;
            result.Add(new ValidatedAnalysisReuseArtifact(stage!, digest!, artifactJson!, canonical.Length));
            if (expectedReuseSource is not null)
            {
                CodeAnalysisReuseStageArtifact? source = expectedReuseSource.Artifacts.SingleOrDefault(a => a.Stage == stage);
                if (source is null
                    || !string.Equals(source.ArtifactDigest, ReadToolString(stages, stage!, "reuseArtifactDigest"), StringComparison.Ordinal)
                    || !string.Equals(DataDigest(data), ReadArtifactDataDigest(source.ArtifactJson), StringComparison.Ordinal))
                {
                    throw new CodeAnalysisValidationException($"Reuse artifact '{stage}' does not match the admitted parent artifact.");
                }
            }
        }

        if (totalBytes > 45 * 1024 || !expected.All(digests.ContainsKey))
        {
            throw new CodeAnalysisValidationException("Reuse artifact set exceeds its bounded request budget.");
        }

        foreach (var name in expected)
        {
            if (!string.Equals(stages.Single(s => s.Stage == name).OutputSha256, digests[name], StringComparison.Ordinal))
            {
                throw new CodeAnalysisValidationException($"Reuse artifact '{name}' is not bound to its stage output.");
            }
        }

        if (reusedStages.Length == 0)
        {
            if (expectedReuseSource is not null)
            {
                throw new CodeAnalysisValidationException("Run parent exists but stages did not report reuse.");
            }
        }
        else if (reusedStages.Length != expected.Length
            || expectedReuseSource is null
            || reusedStages.Any(s => s.ReuseParentRunId?.ToString("D") != expectedReuseSource.SourceRunId
                || s.ReuseParentReportId is null || s.ReuseParentStageId is null)
            || reusedStages.Any(s => !expected.Contains(s.Stage, StringComparer.Ordinal))
            || reusedStages.Select(s => s.ReuseParentStageId).Distinct().Count() != expected.Length
            || expected.Any(name =>
            {
                ValidatedReportStage stage = reusedStages.Single(s => s.Stage == name);
                CodeAnalysisReuseStageArtifact source = expectedReuseSource.Artifacts.Single(a => a.Stage == name);
                return stage.ReuseParentReportId?.ToString("D") != source.SourceReportId
                    || stage.ReuseParentStageId?.ToString("D") != source.SourceStageId
                    || !string.Equals(stage.ReuseArtifactDigest, source.ArtifactDigest, StringComparison.Ordinal);
            }))
        {
            throw new CodeAnalysisValidationException("Reuse parent stage set is incomplete or mismatched.");
        }

        return result;
    }

    private static bool HasReuseBinding(JsonElement binding, string stage, CodeAnalysisRun run, CodeAnalysisPolicyVersion policy)
    {
        var manifest = ApprovedManifest.Read(policy);
        return HasExactProperties(binding,
                "sourceRunId", "sourceInputDigest", "contentSha256", "policyDigest", "materialDigest",
                "declarationDigest", "securityGeneration", "archiveEvidenceId", "malwareEvidenceId",
                "stage", "snapshotId", "snapshotRevision", "modelKey", "indexKey", "modelIdentitySha256")
            && TryStringEquals(binding, "sourceRunId", run.Id.ToString("D"))
            && TryStringEquals(binding, "sourceInputDigest", run.InputDigest)
            && TryStringEquals(binding, "contentSha256", run.ContentSha256)
            && TryStringEquals(binding, "policyDigest", run.PolicyDigest)
            && TryStringEquals(binding, "materialDigest", run.MaterialDigest)
            && TryStringEquals(binding, "declarationDigest", run.DeclarationDigest)
            && TryIntEquals(binding, "securityGeneration", run.SecurityGeneration)
            && TryStringEquals(binding, "archiveEvidenceId", run.ArchiveEvidenceId.ToString("D"))
            && TryStringEquals(binding, "malwareEvidenceId", run.MalwareEvidenceId.ToString("D"))
            && TryStringEquals(binding, "stage", stage)
            && TryStringEquals(binding, "snapshotId", manifest.ReferenceSnapshotId)
            && TryStringEquals(binding, "snapshotRevision", manifest.ReferenceRevision)
            && TryStringEquals(binding, "modelKey", manifest.ModelKey)
            && TryStringEquals(binding, "indexKey", manifest.IndexKey)
            && binding.TryGetProperty("modelIdentitySha256", out JsonElement identity)
            && identity.ValueKind == JsonValueKind.String
            && string.Equals(identity.GetString(), ReadModelIdentityDigest(policy), StringComparison.Ordinal);
    }

    private static string ReadToolString(IReadOnlyList<ValidatedReportStage> stages, string stage, string property)
    {
        using var document = JsonDocument.Parse(stages.Single(s => s.Stage == stage).ToolIdentityJson);
        return document.RootElement.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    }

    private static string DataDigest(JsonElement data) => CanonicalJson.Sha256Hex(data);

    private static string ReadArtifactDataDigest(string artifactJson)
    {
        using var document = JsonDocument.Parse(artifactJson);
        return document.RootElement.TryGetProperty("data", out JsonElement data) ? CanonicalJson.Sha256Hex(data) : "";
    }

    internal static string ReadModelIdentityDigest(CodeAnalysisPolicyVersion policy)
    {
        string[] members = ["checkpointSha256", "loadedStateSha256", "tokenizerHashes", "framing", "pooling", "mask", "preprocessing", "chunking", "dimension", "selectionSha256"];
        using var manifest = JsonDocument.Parse(policy.ToolIdentitiesJson);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!manifest.RootElement.TryGetProperty(member, out JsonElement value))
            {
                return "";
            }
            values.Add(member, value);
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var member in members.Order(StringComparer.Ordinal))
            {
                writer.WritePropertyName(member);
                values[member].WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var identity = JsonDocument.Parse(stream.ToArray());
        return CanonicalJson.Sha256Hex(identity.RootElement);
    }

    internal static bool ValidateReuseData(string stage, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (stage == "EXTRACTION")
        {
            if (!HasExactProperties(data, "analyzed", "omissions", "omissionOverflow", "coverageByDialect", "toolIdentity")
                || !data.TryGetProperty("analyzed", out JsonElement analyzed) || analyzed.ValueKind != JsonValueKind.Array
                || !data.TryGetProperty("omissions", out JsonElement omissions) || omissions.ValueKind != JsonValueKind.Array
                || !data.TryGetProperty("coverageByDialect", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("toolIdentity", out JsonElement tool) || tool.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonElement file in analyzed.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(file, "fileId", "canonicalPath", "sha256", "languageGroup", "dialect", "size", "fragments", "extractionSha")
                    || !file.TryGetProperty("fragments", out JsonElement fragments) || fragments.ValueKind != JsonValueKind.Array
                    || fragments.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.Object || f.TryGetProperty("name", out _)))
                {
                    return false;
                }
            }

            return true;
        }

        if (stage == "TOKEN")
        {
            return HasExactProperties(data, "ranked", "counts", "toolIdentity")
                && data.GetProperty("ranked").ValueKind == JsonValueKind.Object
                && data.GetProperty("counts").ValueKind == JsonValueKind.Object
                && data.GetProperty("toolIdentity").ValueKind == JsonValueKind.Object;
        }

        if (stage != "SEMANTIC"
            || !data.TryGetProperty("windows", out JsonElement windows)
            || windows.ValueKind != JsonValueKind.Array
            || windows.GetArrayLength() > 20000
            || !data.TryGetProperty("counts", out JsonElement counts)
            || counts.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("toolIdentity", out JsonElement semanticTool)
            || semanticTool.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (HasExactProperties(data, "windows", "vectors", "counts", "toolIdentity")
            && data.TryGetProperty("vectors", out JsonElement vectors)
            && vectors.ValueKind == JsonValueKind.Array
            && vectors.GetArrayLength() == windows.GetArrayLength())
        {
            foreach (JsonElement vector in vectors.EnumerateArray())
            {
                if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() != 768
                    || vector.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number
                        || !v.TryGetDouble(out var component) || !double.IsFinite(component)))
                {
                    return false;
                }
            }

            return true;
        }

        if (!HasExactProperties(data, "windows", "vectorsF32LeBase64", "counts", "toolIdentity")
            || !TryReadString(data, "vectorsF32LeBase64", out var encoded))
        {
            return false;
        }

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(encoded!);
        }
        catch (FormatException)
        {
            return false;
        }

        if (packed.Length != windows.GetArrayLength() * 768 * sizeof(float))
        {
            return false;
        }

        for (var offset = 0; offset < packed.Length; offset += sizeof(float))
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(offset, sizeof(float)));
            if (!float.IsFinite(BitConverter.Int32BitsToSingle(bits)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryStringEquals(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool TryReadString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryIntEquals(JsonElement element, string name, int expected) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var actual)
        && actual == expected;

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out JsonElement json)
            && json.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(json.GetString(), "D", out value);
    }

    private static bool HasExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !actual.Add(property.Name))
            {
                return false;
            }
        }

        return actual.SetEquals(expected);
    }

    private static List<ValidatedReportStage> ParseStages(JsonElement stages)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ValidatedReportStage>(8);

        foreach (JsonElement stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Invalid stage record.");
            }

            var name = RequireString(stage, "stage", null);
            if (!CodeAnalysisReportFinalizer.StageNames.Contains(name))
            {
                throw new CodeAnalysisValidationException($"Unexpected stage '{name}'.");
            }

            if (!seen.Add(name))
            {
                throw new CodeAnalysisValidationException($"Duplicate stage '{name}'.");
            }

            RequireString(stage, "applicability", "MANDATORY");
            var disabledVerifier = name == "VERIFIER";
            var outcome = disabledVerifier ? "NOT_APPLICABLE" : "SUCCEEDED";
            RequireString(stage, "outcome", outcome);
            if (disabledVerifier)
            {
                RequireString(stage, "errorCode", "POLICY_DISABLED");
            }

            var errorCode = disabledVerifier ? "POLICY_DISABLED" : null;
            if (!disabledVerifier && stage.TryGetProperty("errorCode", out JsonElement errorJson))
            {
                if (errorJson.ValueKind != JsonValueKind.Null)
                {
                    throw new CodeAnalysisValidationException($"Stage '{name}' must not carry an error.");
                }
            }

            var retryable = false;
            if (stage.TryGetProperty("retryable", out JsonElement retryJson))
            {
                if (retryJson.ValueKind != JsonValueKind.True && retryJson.ValueKind != JsonValueKind.False)
                {
                    throw new CodeAnalysisValidationException("Invalid retryable flag.");
                }

                retryable = retryJson.ValueKind == JsonValueKind.True;
                if (retryable)
                {
                    throw new CodeAnalysisValidationException("Succeeded stage must not be retryable.");
                }
            }


            // Every stage proves what it consumed and produced, and carries the observed tool identity
            // and counts that the stage actually ran with. A stage that omits either is not evidence.
            var inputSha = RequireSha(stage, "inputSha256");
            var outputSha = stage.TryGetProperty("outputSha256", out JsonElement outputJson)
                && outputJson.ValueKind == JsonValueKind.Null
                    ? null
                    : RequireSha(stage, "outputSha256");

            if (!stage.TryGetProperty("toolIdentity", out JsonElement tool) || tool.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException($"Stage '{name}' must report its observed tool identity.");
            }

            if (!stage.TryGetProperty("counts", out JsonElement counts) || counts.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException($"Stage '{name}' must report its counts.");
            }

            // A stage duration is bounded: it must be a finite, non-negative number that no stage in
            // this policy could legitimately exceed, so an unbounded or fabricated cost is refused.
            if (!stage.TryGetProperty("durationSeconds", out JsonElement duration)
                || duration.ValueKind != JsonValueKind.Number
                || !duration.TryGetDouble(out var stageSeconds)
                || !double.IsFinite(stageSeconds)
                || stageSeconds < 0
                || stageSeconds > MAX_STAGE_DURATION_SECONDS)
            {
                throw new CodeAnalysisValidationException($"Stage '{name}' has an out-of-range duration.");
            }

            var durationMs = (int)Math.Round(stageSeconds * 1000);
            var toolJson = tool.GetRawText();
            var countsJson = counts.GetRawText();
            ValidateStageCounts(name, counts);

            Guid? reuseParentRunId = null;
            Guid? reuseParentReportId = null;
            Guid? reuseParentStageId = null;
            string? reuseArtifactDigest = null;
            var hasReuseMarker = tool.TryGetProperty("reused", out JsonElement reusedElement);
            if (hasReuseMarker)
            {
                if (reusedElement.ValueKind != JsonValueKind.True
                    || !TryGuid(tool, "reuseParentRunId", out Guid parentRunId)
                    || !TryGuid(tool, "reuseParentReportId", out Guid parentReportId)
                    || !TryGuid(tool, "reuseParentStageId", out Guid parentStageId)
                    || !tool.TryGetProperty("reuseArtifactDigest", out JsonElement parentDigest)
                    || parentDigest.ValueKind != JsonValueKind.String
                    || !ProductDigests.IsLowercaseSha256Hex(parentDigest.GetString() ?? ""))
                {
                    throw new CodeAnalysisValidationException($"Stage '{name}' has invalid reuse provenance.");
                }

                reuseParentRunId = parentRunId;
                reuseParentReportId = parentReportId;
                reuseParentStageId = parentStageId;
                reuseArtifactDigest = parentDigest.GetString();
            }
            else if (tool.TryGetProperty("reuseParentRunId", out _)
                || tool.TryGetProperty("reuseParentReportId", out _)
                || tool.TryGetProperty("reuseParentStageId", out _)
                || tool.TryGetProperty("reuseArtifactDigest", out _))
            {
                throw new CodeAnalysisValidationException($"Stage '{name}' has partial reuse provenance.");
            }

            result.Add(new ValidatedReportStage(
                name, "MANDATORY", outcome, errorCode, retryable, durationMs, inputSha, outputSha,
                toolJson, countsJson, reuseParentRunId, reuseParentReportId, reuseParentStageId, reuseArtifactDigest));
        }

        if (seen.Count != 8 || result.Count != 8)
        {
            throw new CodeAnalysisValidationException("Report must contain exactly 8 mandatory stages.");
        }

        return result;
    }

    private static void ValidateStageCounts(string stage, JsonElement counts)
    {
        string[] required = stage switch
        {
            "INVENTORY" => ["entries", "files", "expandedBytes"],
            "EXTRACTION" => ["analyzedFiles", "extractedUnits", "coverageByDialect", "omissions"],
            "TOKEN" => ["queryFiles", "batches", "rankedFiles", "comparedGroups", "uncomparedGroups"],
            "SEMANTIC" => ["queryWindows", "rankedFiles"],
            "LICENSE" => ["scannedFiles", "unknownDetections"],
            "UNION" => ["unionFindings"],
            "VERIFIER" => ["outputs"],
            "REPORT" => ["findings", "findingOverflow", "omissions", "omissionOverflow"],
            _ => throw new CodeAnalysisValidationException($"Unexpected stage '{stage}'.")
        };

        foreach (var member in required)
        {
            if (!counts.TryGetProperty(member, out JsonElement value))
            {
                throw new CodeAnalysisValidationException($"Stage '{stage}' is missing count '{member}'.");
            }

            if (member == "coverageByDialect")
            {
                if (value.ValueKind != JsonValueKind.Object
                    || value.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Number
                        || !p.Value.TryGetInt32(out var count) || count < 0))
                {
                    throw new CodeAnalysisValidationException($"Stage '{stage}' has invalid dialect coverage.");
                }
            }
            else if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var count)
                || count < 0)
            {
                throw new CodeAnalysisValidationException($"Stage '{stage}' has invalid count '{member}'.");
            }
        }
    }

    private static void ValidateReportStageCounts(
        IReadOnlyList<ValidatedReportStage> stages,
        int analyzedFiles,
        int omissions,
        int findings,
        int findingOverflow,
        int omissionOverflow)
    {
        JsonElement FindCounts(string name)
        {
            ValidatedReportStage stage = stages.Single(s => s.Stage == name);
            using var document = JsonDocument.Parse(stage.CountsJson);
            return document.RootElement.Clone();
        }

        JsonElement extraction = FindCounts("EXTRACTION");
        JsonElement union = FindCounts("UNION");
        JsonElement report = FindCounts("REPORT");
        if (extraction.GetProperty("analyzedFiles").GetInt32() != analyzedFiles
            || extraction.GetProperty("omissions").GetInt32() != omissions
            || union.GetProperty("unionFindings").GetInt32() != findings + findingOverflow
            || report.GetProperty("findings").GetInt32() != findings
            || report.GetProperty("findingOverflow").GetInt32() != findingOverflow
            || report.GetProperty("omissions").GetInt32() != omissions
            || report.GetProperty("omissionOverflow").GetInt32() != omissionOverflow)
        {
            throw new CodeAnalysisValidationException("Stage counts do not match the report body.");
        }
    }

    private static List<ValidatedReportFinding> ParseFindings(JsonElement findings)
    {
        var result = new List<ValidatedReportFinding>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement finding in findings.EnumerateArray())
        {
            if (finding.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Invalid finding record.");
            }

            RequireOnlyMembers(finding,
            [
                "findingId", "type", "path", "fileId", "startLine", "endLine", "startByte", "endByte",
                "ownExcerpt", "foreign", "channelScore", "reason", "requestedAction"
            ], "finding");

            var key = RequireString(finding, "findingId", null);
            if (!keys.Add(key))
            {
                throw new CodeAnalysisValidationException("Duplicate finding key.");
            }

            var type = RequireString(finding, "type", null);
            if (!_findingTypes.Contains(type))
            {
                throw new CodeAnalysisValidationException($"Unknown finding type '{type}'.");
            }

            if (!finding.TryGetProperty("path", out JsonElement pathJson)
                || pathJson.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(pathJson.GetString())
                || pathJson.GetString()!.Length > 1024)
            {
                throw new CodeAnalysisValidationException("Finding path is missing or outside its bound.");
            }

            var path = pathJson.GetString()!;

            if (!finding.TryGetProperty("ownExcerpt", out JsonElement ownExcerptJson)
                || ownExcerptJson.ValueKind != JsonValueKind.String
                || ownExcerptJson.GetString()!.Length > 320)
            {
                throw new CodeAnalysisValidationException("Finding own excerpt is missing or outside its bound.");
            }

            var ownExcerpt = ownExcerptJson.GetString()!;

            var isScopeLevelNotice = type == "NOTICE_MISSING";
            if (!finding.TryGetProperty("fileId", out JsonElement ownFileId)
                || isScopeLevelNotice && ownFileId.ValueKind != JsonValueKind.Null
                || !isScopeLevelNotice && (ownFileId.ValueKind != JsonValueKind.String
                    || !ProductDigests.IsLowercaseSha256Hex(ownFileId.GetString() ?? "")))
            {
                throw new CodeAnalysisValidationException(isScopeLevelNotice
                    ? "Scope-level notice must not claim a file identity."
                    : "Finding own file identity is missing or invalid.");
            }

            // Foreign sensitivity defaults to PRIVATE fail-closed when absent.
            var sensitivity = "PRIVATE";
            string? foreignId = null;
            string? foreignOrigin = null;
            string? attribution = null;
            if (finding.TryGetProperty("foreign", out JsonElement foreign) && foreign.ValueKind == JsonValueKind.Object)
            {
                RequireOnlyMembers(foreign, ["fileId", "sensitivity", "origin", "attribution"], "foreign finding");
                if (!foreign.TryGetProperty("sensitivity", out JsonElement sens) || sens.ValueKind != JsonValueKind.String)
                {
                    throw new CodeAnalysisValidationException("Foreign finding sensitivity is missing.");
                }

                sensitivity = sens.GetString() ?? "";

                if (sensitivity != "PUBLIC" && sensitivity != "PRIVATE")
                {
                    throw new CodeAnalysisValidationException("Unknown finding sensitivity.");
                }

                if (!foreign.TryGetProperty("fileId", out JsonElement sid)
                    || sid.ValueKind != JsonValueKind.String
                    || !ProductDigests.IsLowercaseSha256Hex(sid.GetString() ?? ""))
                {
                    throw new CodeAnalysisValidationException("Foreign finding identity is missing or invalid.");
                }
                foreignId = sid.GetString();

                if (!foreign.TryGetProperty("origin", out JsonElement origin)
                    || origin.ValueKind != JsonValueKind.String
                    || origin.GetString() is not ("OWN" or "FOREIGN"))
                {
                    throw new CodeAnalysisValidationException("Foreign finding origin is missing or invalid.");
                }

                foreignOrigin = origin.GetString();

                if (foreign.TryGetProperty("attribution", out JsonElement attr))
                {
                    if (attr.ValueKind != JsonValueKind.Object
                        || attr.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String
                            || p.Value.GetString()!.Length > 512))
                    {
                        throw new CodeAnalysisValidationException("Foreign attribution is outside its bound.");
                    }

                    if (sensitivity == "PUBLIC" && attr.EnumerateObject().Any())
                    {
                        if (!attr.TryGetProperty("label", out JsonElement label)
                            || label.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(label.GetString()))
                        {
                            throw new CodeAnalysisValidationException("Public attribution is missing its label.");
                        }

                        attribution = attr.GetRawText();
                    }
                    else if (sensitivity == "PRIVATE" && attr.EnumerateObject().Any())
                    {
                        throw new CodeAnalysisValidationException("Private finding must not carry attribution.");
                    }
                }
            }

            if (sensitivity == "PUBLIC" && foreignId is not null && attribution is null)
            {
                throw new CodeAnalysisValidationException("Public finding must carry public attribution.");
            }

            var reasonText = RequireBoundedFindingText(finding, "reason", 1024);
            var requestedAction = RequireBoundedFindingText(finding, "requestedAction", 512);

            // Byte offsets are not synthesized from line coordinates; Python reports UTF-8 byte
            // offsets only when it has them. Line coordinates remain separate presentation data.
            var startByte = ReadOptionalCoordinate(finding, "startByte", minimum: 0);
            var endByte = ReadOptionalCoordinate(finding, "endByte", minimum: 0);
            if (startByte.HasValue != endByte.HasValue || startByte.HasValue && endByte < startByte)
            {
                throw new CodeAnalysisValidationException("Finding byte coordinates are incomplete or reversed.");
            }

            var startLine = ReadOptionalCoordinate(finding, "startLine", minimum: 1);
            var endLine = ReadOptionalCoordinate(finding, "endLine", minimum: 1);
            if (startLine.HasValue != endLine.HasValue || startLine.HasValue && endLine < startLine)
            {
                throw new CodeAnalysisValidationException("Finding line coordinates are incomplete or reversed.");
            }

            if (isScopeLevelNotice)
            {
                if (ownExcerpt != "" || startByte.HasValue || endByte.HasValue || startLine.HasValue || endLine.HasValue
                    || finding.TryGetProperty("foreign", out JsonElement noticeForeign)
                        && noticeForeign.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    throw new CodeAnalysisValidationException("Scope-level notice must not carry file evidence.");
                }
            }

            double? tokenScore = null;
            double? semanticScore = null;
            int? tokenRank = null;
            int? semanticRank = null;
            if (finding.TryGetProperty("channelScore", out JsonElement scoreJson))
            {
                if (scoreJson.ValueKind != JsonValueKind.Number || !scoreJson.TryGetDouble(out var cs))
                {
                    throw new CodeAnalysisValidationException("Finding channel score is invalid.");
                }

                if (!double.IsFinite(cs) || cs is < 0 or > 1)
                {
                    throw new CodeAnalysisValidationException("Finding channel score is out of bounds.");
                }

                if (type == "SIMILARITY_TOKEN")
                {
                    tokenScore = cs;
                }
                else if (type == "SIMILARITY_SEMANTIC")
                {
                    semanticScore = cs;
                }
                else
                {
                    throw new CodeAnalysisValidationException("Only similarity findings may carry a channel score.");
                }
            }

            if (type == "SIMILARITY_TOKEN" && !tokenScore.HasValue
                || type == "SIMILARITY_SEMANTIC" && !semanticScore.HasValue
                || type is not ("SIMILARITY_TOKEN" or "SIMILARITY_SEMANTIC") && scoreJson.ValueKind != JsonValueKind.Undefined)
            {
                throw new CodeAnalysisValidationException("Finding score does not match its channel type.");
            }

            result.Add(new ValidatedReportFinding(
                key, type, path, ownExcerpt, startByte, endByte, foreignId, foreignOrigin, sensitivity, attribution,
                tokenScore, semanticScore, tokenRank, semanticRank,
                ReasonText: reasonText,
                RequestedAction: requestedAction,
                OwnStartLine: startLine,
                OwnEndLine: endLine));
        }

        return result;
    }

    private static string RequireBoundedFindingText(JsonElement finding, string name, int maximumLength)
    {
        if (!finding.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw new CodeAnalysisValidationException($"Finding '{name}' is missing or outside its bound.");
        }

        return value.GetString()!;
    }

    private static int? ReadOptionalCoordinate(JsonElement element, string name, int minimum)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var coordinate)
            || coordinate < minimum)
        {
            throw new CodeAnalysisValidationException($"Finding coordinate '{name}' is invalid.");
        }

        return coordinate;
    }

    private static void RequireOnlyMembers(JsonElement element, IReadOnlyCollection<string> allowed, string description)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new CodeAnalysisValidationException($"Unknown {description} field '{property.Name}'.");
            }
        }
    }

    private static string RecomputeReportId(JsonElement root)
    {
        // Rebuild the pre-REPORT body: drop reportId and sellerSafe, drop the REPORT stage.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("reportId") || property.NameEquals("sellerSafe"))
                {
                    continue;
                }

                if (property.NameEquals("stages") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("stages");
                    writer.WriteStartArray();
                    foreach (JsonElement stage in property.Value.EnumerateArray())
                    {
                        if (stage.ValueKind == JsonValueKind.Object
                            && stage.TryGetProperty("stage", out JsonElement name)
                            && name.ValueKind == JsonValueKind.String
                            && string.Equals(name.GetString(), "REPORT", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        stage.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var preReport = JsonDocument.Parse(buffer.ToArray());
        return CanonicalJson.Sha256Hex(preReport.RootElement);
    }

    private static string RequireString(JsonElement element, string name, string? expected, bool ordinalIgnoreCase = false)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new CodeAnalysisValidationException($"Missing string '{name}'.");
        }

        var actual = value.GetString() ?? "";
        if (expected is not null)
        {
            StringComparison comparison = ordinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(actual, expected, comparison))
            {
                throw new CodeAnalysisValidationException($"Binding mismatch on '{name}'.");
            }
        }

        return actual;
    }

    /// <summary>
    /// The owner-safe projection may carry only the bounded fields the analysis service projects:
    /// the finding identity, type, own path and excerpt, and generic wording. It has to project
    /// exactly the report's findings, in order, so foreign identity, attribution detail, scores and
    /// vectors are refused outright and a projection that hides or invents a finding cannot reach a
    /// seller-facing consumer.
    /// </summary>
    private static void ValidateSellerSafeFindings(
        JsonElement sellerSafe,
        List<ValidatedReportFinding> reportFindings)
    {
        if (!sellerSafe.TryGetProperty("findings", out JsonElement findings)
            || findings.ValueKind != JsonValueKind.Array)
        {
            throw new CodeAnalysisValidationException("Seller projection must carry a findings array.");
        }

        if (findings.GetArrayLength() != reportFindings.Count)
        {
            throw new CodeAnalysisValidationException("Seller projection must project every reported finding.");
        }

        var index = 0;
        foreach (JsonElement finding in findings.EnumerateArray())
        {
            if (finding.ValueKind != JsonValueKind.Object)
            {
                throw new CodeAnalysisValidationException("Seller projection finding must be an object.");
            }

            foreach (JsonProperty property in finding.EnumerateObject())
            {
                if (!_sellerSafeFindingMembers.Contains(property.Name))
                {
                    throw new CodeAnalysisValidationException(
                        $"Seller projection must not carry '{property.Name}'.");
                }
            }

            RequireString(finding, "findingId", reportFindings[index].FindingKey);
            RequireString(finding, "type", reportFindings[index].FindingType);
            RequireString(finding, "path", reportFindings[index].OwnScopePath);
            RequireString(finding, "ownExcerpt", reportFindings[index].OwnExcerpt);
            RequireString(finding, "reason", reportFindings[index].ReasonText);
            RequireString(finding, "requestedAction", reportFindings[index].RequestedAction);
            RequireString(finding, "basis", null);
            index++;
        }
    }

    private static readonly HashSet<string> _sellerSafeFindingMembers = new(StringComparer.Ordinal)
    {
        "findingId",
        "type",
        "path",
        "startLine",
        "endLine",
        "ownExcerpt",
        "basis",
        "reason",
        "requestedAction"
    };

    private static void RequireGuid(JsonElement element, string name, Guid expected)
    {
        var actual = RequireString(element, name, null);
        if (!Guid.TryParse(actual, out Guid parsed) || parsed != expected)
        {
            throw new CodeAnalysisValidationException($"Binding mismatch on '{name}'.");
        }
    }

    private static void RequireInt(JsonElement element, string name, int expected)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual) || actual != expected)
        {
            throw new CodeAnalysisValidationException($"Binding mismatch on '{name}'.");
        }
    }

    private static int RequireNonNegativeInt(JsonElement element, string name, bool allowMissing = false)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            if (allowMissing)
            {
                return 0;
            }

            throw new CodeAnalysisValidationException($"Missing integer '{name}'.");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual) || actual < 0)
        {
            throw new CodeAnalysisValidationException($"Invalid integer '{name}'.");
        }

        return actual;
    }

    private static string RequireSha(JsonElement element, string name)
    {
        var value = RequireString(element, name, null);
        if (value.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$"))
        {
            throw new CodeAnalysisValidationException($"Invalid digest '{name}'.");
        }

        return value;
    }

    private static string? OptionalSha(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CodeAnalysisValidationException($"Invalid digest '{name}'.");
        }

        var text = value.GetString() ?? "";
        if (text.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-f]{64}$"))
        {
            throw new CodeAnalysisValidationException($"Invalid digest '{name}'.");
        }

        return text;
    }
}

internal sealed class CodeAnalysisValidationException : Exception
{
    public CodeAnalysisValidationException(string message)
        : base(message)
    {
    }

    public CodeAnalysisValidationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
