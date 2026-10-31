using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Reproduces the analysis service's real response envelopes byte-for-byte in shape, so a test can
/// never satisfy the server with a body the service would never send.
/// <para>
/// The completed envelope carries the same bindings the service binds (schema, run, input digest,
/// content hash, policy version and digest, verifier mode, security evidence, reservation), the same
/// eight mandatory stages, and the per-stage tool identity and counts each real stage reports:
/// inventory entry counts, the extraction parser map, the observed dolos token identity with its k
/// and window, the bound encoder identity with model key, index key, loaded checkpoint and state,
/// framing, pooling and device, the observed scancode licence identity, union finding counts, the
/// evaluation-disabled verifier mode, and the report input and output digests.
/// </para>
/// <para>
/// The failure envelopes reproduce the service's CANCELLED and FAILED envelopes exactly: empty stage
/// and finding lists, unsupported coverage, an explicit error code, the retryable flag, and a null
/// admission authority.
/// </para>
/// </summary>
internal static class PythonAnalysisEnvelope
{
    /// <summary>The service version string the deployed service reports.</summary>
    private const string SERVICE_VERSION = "1.0.0";

    public sealed record Bindings(
        Guid RunId,
        Guid AssetId,
        Guid AssetVersionId,
        string InputDigest,
        string ContentSha256,
        string PolicyDigest,
        Guid ArchiveEvidenceId,
        Guid MalwareEvidenceId,
        int SecurityGeneration,
        string ServiceKey,
        int ReservationGeneration);

    public sealed record Options
    {
        public string Schema { get; init; } = "code-analysis-v1";
        public string RunId { get; init; } = "";
        public string InputDigest { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public string AssetId { get; init; } = "";
        public string VersionId { get; init; } = "";
        public string Outcome { get; init; } = "COMPLETED";
        public string? ErrorCode { get; init; }
        public bool Retryable { get; init; }
        public bool AdmittedByFabricated { get; init; }
        public bool IncludeFailureStages { get; init; }
        public bool IncludeFailureFindings { get; init; }
        public string? PolicyVersion { get; init; }
        public string? FindingReason { get; init; }
        public string? FailedStage { get; init; }
        public bool FailureCarriesFailedStage { get; init; }
        public bool OmitRetryable { get; init; }
        public bool RetryableIsNotBoolean { get; init; }
        public string? VerifierMode { get; init; }
    }

    public static Bindings Bind(CodeAnalysisRun run) =>
        new(run.Id,
            run.AssetId,
            run.AssetVersionId,
            run.InputDigest,
            run.ContentSha256,
            run.PolicyDigest,
            run.ArchiveEvidenceId,
            run.MalwareEvidenceId,
            run.SecurityGeneration,
            run.ServiceKey,
            run.ReservationGeneration ?? 1);

    public static Bindings Bind(
        Guid runId,
        Guid assetId,
        Guid assetVersionId,
        string inputDigest,
        string contentSha256,
        string policyDigest,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string serviceKey,
        int reservationGeneration) =>
        new(runId, assetId, assetVersionId, inputDigest, contentSha256, policyDigest,
            archiveEvidenceId, malwareEvidenceId, securityGeneration, serviceKey, reservationGeneration);

    /// <summary>
    /// The service's execution-failure envelope, in the exact shape its producer emits: a stage
    /// failure carries the observed stage records, the restated approved policy and the stage that
    /// actually failed; a cleanup failure keeps the completed stage records and its cleanup error; an
    /// unhandled exception and a cancellation carry no stages and no policy.
    /// </summary>
    public static string FailureEnvelope(
        Bindings bindings,
        Options? options = null,
        FailureShape shape = FailureShape.STAGE_FAILURE,
        CodeAnalysisPolicyVersion? policy = null)
    {
        Options applied = Apply(options, bindings);
        var cleanupFailed = shape == FailureShape.CLEANUP_FAILURE;
        var stageFailure = shape == FailureShape.STAGE_FAILURE;

        if (cleanupFailed)
        {
            ArgumentNullException.ThrowIfNull(policy);
            JsonObject completed = JsonNode.Parse(CompletedEnvelope(bindings, policy))!.AsObject();
            return new JsonObject
            {
                ["schema"] = applied.Schema,
                ["serviceVersion"] = SERVICE_VERSION,
                ["outcome"] = "FAILED",
                ["runId"] = applied.RunId,
                ["inputDigest"] = applied.InputDigest,
                ["contentSha256"] = applied.ContentSha256,
                ["assetId"] = applied.AssetId,
                ["versionId"] = applied.VersionId,
                ["policy"] = completed["policy"]!.DeepClone(),
                ["securityEvidence"] = completed["securityEvidence"]!.DeepClone(),
                ["reservation"] = completed["reservation"]!.DeepClone(),
                ["stages"] = completed["stages"]!.DeepClone(),
                ["coverage"] = completed["coverage"]!.DeepClone(),
                ["findings"] = new JsonArray(),
                ["findingOverflow"] = completed["findingOverflow"]!.DeepClone(),
                ["errorCode"] = "CLEANUP_FAILED",
                ["retryable"] = false,
                ["failedStage"] = "REPORT",
                ["admittedBy"] = null,
                ["cleanupError"] = "CLEANUP_FAILED"
            }.ToJsonString();
        }

        JsonArray stages = new();
        if (stageFailure)
        {
            stages.Add(Stage("INVENTORY", "SUCCEEDED", AdmittedReportTestEvidence.NewHex64(),
                new JsonObject { ["entries"] = 3 }, new JsonObject()));
            stages.Add(Stage("EXTRACTION", "SUCCEEDED", AdmittedReportTestEvidence.NewHex64(),
             new JsonObject
             {
                 ["analyzedFiles"] = 1,
                 ["extractedUnits"] = 1,
                 ["coverageByDialect"] = new JsonObject { ["csharp"] = 1, ["javascript"] = 0 },
                 ["omissions"] = 0
             },
                new JsonObject { ["parsers"] = new JsonObject { ["csharp"] = "tree-sitter-c-sharp" } }));
            stages.Add(FailedStage(applied.FailedStage ?? "SEMANTIC", applied.ErrorCode ?? "MODEL_UNAVAILABLE", applied.Retryable));
        }
        else if (applied.IncludeFailureStages)
        {
            stages.Add(Stage("INVENTORY", "SUCCEEDED", AdmittedReportTestEvidence.NewHex64(),
                new JsonObject { ["entries"] = 1 }, new JsonObject()));
        }

        var envelope = new JsonObject
        {
            ["schema"] = applied.Schema,
            ["serviceVersion"] = SERVICE_VERSION,
            ["outcome"] = applied.Outcome,
            ["runId"] = applied.RunId,
            ["inputDigest"] = applied.InputDigest,
            ["contentSha256"] = applied.ContentSha256,
            ["assetId"] = applied.AssetId,
            ["versionId"] = applied.VersionId,
            ["stages"] = stages,
            ["coverage"] = stageFailure
                ? new JsonObject
                {
                    ["level"] = "PARTIAL",
                    ["analyzedFiles"] = 1,
                    ["omissions"] = new JsonArray { new JsonObject { ["reason"] = "SEMANTIC_UNAVAILABLE" } },
                    ["omissionOverflow"] = 0
                }
                : new JsonObject
                {
                    ["level"] = "UNSUPPORTED",
                    ["analyzedFiles"] = 0,
                    ["omissions"] = new JsonArray(),
                    ["omissionOverflow"] = 0
                },
            ["findings"] = applied.IncludeFailureFindings
                ? new JsonArray(Finding("finding-1", applied.FindingReason))
                : new JsonArray(),
            ["errorCode"] = applied.ErrorCode ?? "MODEL_UNAVAILABLE",
            ["retryable"] = applied.Retryable,
            ["admittedBy"] = applied.AdmittedByFabricated ? "python-service" : null,
            ["cleanupError"] = null
        };

        if (stageFailure && policy is not null)
        {
            envelope["policy"] = new JsonObject
            {
                ["policyVersion"] = policy.PolicyKey,
                ["policyDigest"] = policy.PolicyDigest,
                ["verifierMode"] = policy.VerifierMode,
            };
        }

        if (stageFailure)
        {
            envelope["failedStage"] = applied.FailedStage ?? "SEMANTIC";
        }
        else if (applied.FailureCarriesFailedStage)
        {
            envelope["failedStage"] = "SEMANTIC";
        }

        if (applied.OmitRetryable)
        {
            envelope.Remove("retryable");
        }
        else if (applied.RetryableIsNotBoolean)
        {
            envelope["retryable"] = "true";
        }

        return envelope.ToJsonString();
    }

    /// <summary>The exact failure shapes the analysis service's producers emit.</summary>
    public enum FailureShape
    {
        /// <summary>A mandatory stage failed: observed stage records, restated policy, failed stage.</summary>
        STAGE_FAILURE,
        /// <summary>An unhandled service exception: no stages, no policy, no failed stage.</summary>
        EXCEPTION,
        /// <summary>The run was cancelled: no stages, no policy, retryable only for an operational cause.</summary>
        CANCELLATION,
        /// <summary>Plaintext cleanup failed after the analysis completed: stages retained.</summary>
        CLEANUP_FAILURE
    }

    private static JsonObject FailedStage(string stage, string errorCode, bool retryable) =>
        new()
        {
            ["stage"] = stage,
            ["applicability"] = "MANDATORY",
            ["outcome"] = "FAILED",
            ["errorCode"] = errorCode,
            ["retryable"] = retryable,
            ["durationSeconds"] = 0.5,
            ["inputSha256"] = AdmittedReportTestEvidence.NewHex64(),
            ["outputSha256"] = null,
            ["toolIdentity"] = new JsonObject(),
            ["counts"] = new JsonObject()
        };

    /// <summary>
    /// Applies a semantic or provenance forgery and then regenerates every derived identity exactly as
    /// the service would: the report identity over the pre-REPORT body, the REPORT stage input digest
    /// over the mutated finding/coverage/stage summary, and the seller-safe projection bound to the
    /// recomputed report. The result is an internally self-consistent envelope whose only defect is
    /// the forged field, so a rejection can only come from the server validating that field rather
    /// than from a digest that no longer matches.
    /// </summary>
    public static string CompletedEnvelopeWithSelfConsistentForgery(
        Bindings bindings,
        CodeAnalysisPolicyVersion policy,
        Func<JsonObject, JsonObject> forge,
        string declarationDigest = "",
        string materialDigest = "") =>
        CompletedEnvelope(
            bindings,
            policy,
            options: null,
            declaredLicensesJson: "[]",
            declarationDigest,
            materialDigest,
            mutate: body => RegenerateDerivedIdentity(forge(body)));

    /// <summary>
    /// Recomputes the report identity, the REPORT stage input digest and the owner-safe projection
    /// from an already-mutated body, so a forged envelope carries identities that agree with it.
    /// </summary>
    public static JsonObject RegenerateDerivedIdentity(JsonObject body)
    {
        JsonObject mutated = body.DeepClone().AsObject();

        JsonObject preReport = mutated.DeepClone().AsObject();
        preReport.Remove("reportId");
        preReport.Remove("sellerSafe");

        var preStages = (JsonArray)preReport["stages"]!.AsArray().DeepClone();
        preReport["stages"] = new JsonArray();
        var stageNames = new List<string>();
        foreach (JsonNode? stage in preStages)
        {
            var name = stage?["stage"]?.GetValue<string>();
            if (string.Equals(name, "REPORT", StringComparison.Ordinal))
            {
                continue;
            }

            stageNames.Add(name!);
            preReport["stages"]!.AsArray().Add(stage?.DeepClone());
        }

        var reportInput = new JsonObject
        {
            ["findings"] = new JsonArray(
                ((JsonArray)mutated["findings"]!).Select(f => f!["findingId"]!.DeepClone()).ToArray()),
            ["coverage"] = mutated["coverage"]!.DeepClone(),
            ["stages"] = new JsonArray(stageNames.Select(n => (JsonNode)n).ToArray())
        };
        using var reportInputDocument = JsonDocument.Parse(reportInput.ToJsonString());
        var reportInputSha256 = CanonicalJson.Sha256Hex(reportInputDocument.RootElement);

        using var preDocument = JsonDocument.Parse(preReport.ToJsonString());
        var reportId = CanonicalJson.Sha256Hex(preDocument.RootElement);

        JsonArray keptStages = new();
        foreach (JsonNode? stage in preStages)
        {
            var name = stage?["stage"]?.GetValue<string>();
            if (string.Equals(name, "REPORT", StringComparison.Ordinal))
            {
                continue;
            }

            keptStages.Add(stage?.DeepClone());
        }

        var overflow = 0;
        if (mutated.TryGetPropertyValue("findingOverflow", out JsonNode? on) && on != null)
        {
            overflow = on.GetValue<int>();
        }

        var reportStage = new JsonObject
        {
            ["stage"] = "REPORT",
            ["applicability"] = "MANDATORY",
            ["outcome"] = "SUCCEEDED",
            ["errorCode"] = null,
            ["retryable"] = false,
            ["durationSeconds"] = 0.0,
            ["inputSha256"] = reportInputSha256,
            ["outputSha256"] = reportId,
            ["toolIdentity"] = new JsonObject { ["mode"] = "DISABLED_BY_EVALUATION" },
            ["counts"] = new JsonObject
            {
                ["findings"] = ((JsonArray)mutated["findings"]!).Count,
                ["findingOverflow"] = overflow,
                ["omissions"] = ((JsonArray)mutated["coverage"]!["omissions"]!).Count,
                ["omissionOverflow"] = mutated["coverage"]!["omissionOverflow"]!.DeepClone()
            }
        };
        keptStages.Add(reportStage);

        mutated["stages"] = keptStages;
        mutated["reportId"] = reportId;
        mutated["findingOverflow"] = overflow;
        mutated["sellerSafe"] = BuildSellerSafe(mutated, reportId);
        return mutated;
    }
    /// <summary>The owner-safe projection the service builds for a completed report.</summary>
    private static JsonObject BuildSellerSafe(JsonObject body, string reportId)
    {
        JsonArray safeFindings = new();
        foreach (JsonNode? node in body["findings"]!.AsArray())
        {
            JsonObject finding = node!.AsObject();
            var attribution = finding["foreign"]?["attribution"]?["label"]?.GetValue<string>();
            var basis = attribution is null
                ? "Declared license scope has no scanned files."
                : $"Overlaps public reference {attribution[..Math.Min(attribution.Length, 160)]}.";

            safeFindings.Add(new JsonObject
            {
                ["findingId"] = finding["findingId"]!.DeepClone(),
                ["type"] = finding["type"]!.DeepClone(),
                ["path"] = finding["path"]!.DeepClone(),
                ["startLine"] = finding["startLine"]?.DeepClone(),
                ["endLine"] = finding["endLine"]?.DeepClone(),
                ["ownExcerpt"] = finding["ownExcerpt"]?.DeepClone(),
                ["basis"] = basis,
                ["reason"] = finding["reason"]!.DeepClone(),
                ["requestedAction"] = finding["requestedAction"]!.DeepClone()
            });
        }

        return new JsonObject
        {
            ["reportId"] = reportId,
            ["runId"] = body["runId"]!.DeepClone(),
            ["outcome"] = body["outcome"]!.DeepClone(),
            ["coverage"] = body["coverage"]!["level"]!.DeepClone(),
            ["omissionCount"] = body["coverage"]!["omissions"]!.AsArray().Count,
            ["findings"] = safeFindings,
            ["findingOverflow"] = 0,
        };
    }


    public static string CompletedEnvelope(
        Bindings bindings,
        CodeAnalysisPolicyVersion policy,
        Options? options = null,
        string? declaredLicensesJson = null,
        string declarationDigest = "",
        string materialDigest = "",
        Func<JsonObject, JsonObject>? mutate = null)
    {
        Options applied = Apply(options, bindings);

        JsonObject preReport = new()
        {
            ["schema"] = applied.Schema,
            ["runId"] = applied.RunId,
            ["inputDigest"] = applied.InputDigest,
            ["contentSha256"] = applied.ContentSha256,
            ["assetId"] = applied.AssetId,
            ["versionId"] = applied.VersionId,
            ["serviceVersion"] = SERVICE_VERSION,
            ["outcome"] = "COMPLETED",
            ["admittedBy"] = null,
            ["cleanupError"] = null,
            ["policy"] = new JsonObject
            {
                ["policyVersion"] = applied.PolicyVersion ?? policy.PolicyKey,
                ["policyDigest"] = policy.PolicyDigest,
                ["verifierMode"] = applied.VerifierMode ?? policy.VerifierMode,
                ["verifierReason"] = policy.VerifierReason,
            },
            ["declaration"] = new JsonObject
            {
                ["digest"] = declarationDigest,
                ["licenses"] = JsonNode.Parse(declaredLicensesJson ?? "[]")!.AsArray()
            },
            ["material"] = new JsonObject { ["digest"] = materialDigest },
            ["securityEvidence"] = new JsonObject
            {
                ["contentSha256"] = applied.ContentSha256,
                ["archiveEvidenceId"] = bindings.ArchiveEvidenceId.ToString("D"),
                ["malwareEvidenceId"] = bindings.MalwareEvidenceId.ToString("D"),
                ["securityGeneration"] = bindings.SecurityGeneration
            },
            ["reservation"] = new JsonObject
            {
                ["serviceKey"] = bindings.ServiceKey,
                ["slotNumber"] = 0,
                ["generation"] = bindings.ReservationGeneration
            },
            ["stages"] = new JsonArray(
                InventoryStage(),
                ExtractionStage(),
                TokenStage(),
                SemanticStage(),
                LicenseStage(),
                UnionStage(),
                VerifierStage()),
            ["coverage"] = new JsonObject
            {
                ["level"] = "FULL",
                ["analyzedFiles"] = 1,
                ["omissions"] = new JsonArray(),
                ["omissionOverflow"] = 0
            },
            ["findings"] = new JsonArray(Finding("finding-1", applied.FindingReason)),
            ["findingOverflow"] = 0,
            ["comparedScope"] = new JsonObject
            {
                ["snapshotId"] = policy.ReferenceSnapshotId,
                ["revision"] = "revision-1",
                ["comparedFiles"] = 1
            }
        };

        preReport["policy"]!["evidenceSha256"] = ReviewAnalysisPolicy.EVIDENCE_SHA256;

        // The report identity is the digest of the pre-REPORT body, exactly as the service computes it.
        using var preDocument = JsonDocument.Parse(preReport.ToJsonString());
        var reportId = CanonicalJson.Sha256Hex(preDocument.RootElement);

        JsonArray stages = new();
        foreach (JsonNode? stage in preReport["stages"]!.AsArray())
        {
            stages.Add(stage?.DeepClone());
        }

        // The REPORT stage records the digest of its exact input: the kept finding keys, the coverage
        // block and the stage names it summarizes, and outputs the report identity.
        JsonObject reportInput = new()
        {
            ["findings"] = new JsonArray { "finding-1" },
            ["coverage"] = preReport["coverage"]!.DeepClone(),
            ["stages"] = new JsonArray(
                "INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE", "UNION", "VERIFIER")
        };
        using var reportInputDocument = JsonDocument.Parse(reportInput.ToJsonString());
        var reportInputSha256 = CanonicalJson.Sha256Hex(reportInputDocument.RootElement);

        stages.Add(ReportStage(reportInputSha256, reportId));

        preReport["stages"] = stages;
        preReport["reportId"] = reportId;

        // The owner-safe projection is mandatory on a completed report: it projects exactly the
        // report's findings to owner-safe fields only, with generic wording instead of foreign
        // identity, attribution detail, scores and vectors.
        JsonArray safeFindings = new();
        foreach (JsonNode? node in preReport["findings"]!.AsArray())
        {
            JsonObject finding = node!.AsObject();
            var attribution = ((JsonObject)finding["foreign"]!["attribution"]!)["label"]!.GetValue<string>();

            safeFindings.Add(new JsonObject
            {
                ["findingId"] = finding["findingId"]!.DeepClone(),
                ["type"] = finding["type"]!.DeepClone(),
                ["path"] = finding["path"]!.DeepClone(),
                ["startLine"] = finding["startLine"]!.DeepClone(),
                ["endLine"] = finding["endLine"]!.DeepClone(),
                ["ownExcerpt"] = finding["ownExcerpt"]!.DeepClone(),
                ["basis"] = $"Overlaps public reference {attribution[..Math.Min(attribution.Length, 160)]}.",
                ["reason"] = finding["reason"]!.DeepClone(),
                ["requestedAction"] = finding["requestedAction"]!.DeepClone()
            });
        }

        preReport["sellerSafe"] = new JsonObject
        {
            ["reportId"] = reportId,
            ["runId"] = applied.RunId,
            ["outcome"] = "COMPLETED",
            ["coverage"] = "FULL",
            ["omissionCount"] = 0,
            ["findings"] = safeFindings,
            ["findingOverflow"] = 0,
        };

        // The mutation is applied to the complete envelope (with reportId, sellerSafe, REPORT stage),
        // not the pre-report body. This allows mutations that depend on the final envelope structure.
        JsonObject envelope = preReport.DeepClone().AsObject();
        JsonObject mutatedEnvelope = mutate is null ? envelope : mutate(envelope);
        return mutatedEnvelope.ToJsonString();
    }

    private static Options Apply(Options? options, Bindings bindings)
    {
        Options applied = options ?? new Options();
        return new Options
        {
            Schema = applied.Schema,
            RunId = applied.RunId.Length == 0 ? bindings.RunId.ToString("D") : applied.RunId,
            InputDigest = applied.InputDigest.Length == 0 ? bindings.InputDigest : applied.InputDigest,
            ContentSha256 = applied.ContentSha256.Length == 0 ? bindings.ContentSha256 : applied.ContentSha256,
            AssetId = applied.AssetId.Length == 0 ? bindings.AssetId.ToString("D") : applied.AssetId,
            VersionId = applied.VersionId.Length == 0 ? bindings.AssetVersionId.ToString("D") : applied.VersionId,
            Outcome = applied.Outcome,
            ErrorCode = applied.ErrorCode,
            Retryable = applied.Retryable,
            AdmittedByFabricated = applied.AdmittedByFabricated,
            IncludeFailureStages = applied.IncludeFailureStages,
            IncludeFailureFindings = applied.IncludeFailureFindings,
            PolicyVersion = applied.PolicyVersion,
            VerifierMode = applied.VerifierMode
        };
    }

    private static JsonObject InventoryStage() =>
        Stage("INVENTORY",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
            new JsonObject { ["entries"] = 3, ["files"] = 1, ["expandedBytes"] = 32 },
            new JsonObject());

    private static JsonObject ExtractionStage() =>
        Stage("EXTRACTION",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
            new JsonObject
            {
                ["analyzedFiles"] = 1,
                ["extractedUnits"] = 1,
                ["coverageByDialect"] = new JsonObject { ["csharp"] = 1, ["javascript"] = 0 },
                ["omissions"] = 0
            },
            new JsonObject
            {
                ["parsers"] = new JsonObject
                {
                    ["csharp"] = "tree-sitter-c-sharp",
                    ["javascript"] = "tree-sitter-javascript"
                }
            });

    private static JsonObject TokenStage() =>
        Stage("TOKEN",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
            new JsonObject
            {
                ["queryFiles"] = 1,
                ["batches"] = 1,
                ["rankedFiles"] = 1,
                ["comparedGroups"] = 1,
                ["uncomparedGroups"] = 0
            },
            new JsonObject
            {
                ["name"] = "dolos",
                ["version"] = "0.1.2",
                ["k"] = 23,
                ["window"] = 17,
                ["available"] = true
            });

    private static JsonObject SemanticStage() =>
        Stage("SEMANTIC",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
            new JsonObject { ["queryWindows"] = 1, ["rankedFiles"] = 1 },
            new JsonObject
            {
                ["encoder"] = "unixcoder-base-nine",
                ["modelKey"] = AdmittedReportTestEvidence.MODEL_KEY,
                ["indexKey"] = AdmittedReportTestEvidence.INDEX_KEY,
                ["loadedCheckpointSha256"] = new string('a', 64),
                ["loadedStateSha256"] = new string('d', 64),
                ["framing"] = "encoder-only",
                ["pooling"] = "padding-masked mean then L2",
                ["tokenizerHashes"] = new JsonObject { ["tokenizer.json"] = new string('b', 64) },
                ["selectionSha256"] = new string('c', 64),
                ["mask"] = "padding attention mask",
                ["preprocessing"] = "utf-8 strict, no execution",
                ["chunking"] = "encoder-only-256-overlap32-max16-v1",
                ["dimension"] = 768,
                ["device"] = "cpu"
            });

    private static JsonObject LicenseStage() =>
        Stage("LICENSE",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
             new JsonObject { ["scannedFiles"] = 1, ["unknownDetections"] = 0 },
            new JsonObject
            {
                ["name"] = "scancode",
                ["version"] = "32.3.0",
                ["available"] = true
            });

    private static JsonObject UnionStage() =>
        Stage("UNION",
            "SUCCEEDED",
            AdmittedReportTestEvidence.NewHex64(),
            new JsonObject { ["unionFindings"] = 1 },
            new JsonObject());

    private static JsonObject VerifierStage()
    {
        JsonObject stage = Stage("VERIFIER", "NOT_APPLICABLE", AdmittedReportTestEvidence.NewHex64(),
            new JsonObject { ["outputs"] = 0 }, new JsonObject
            {
                ["mode"] = ReviewAnalysisPolicy.VERIFIER_MODE,
                ["reason"] = ReviewAnalysisPolicy.VERIFIER_REASON,
                ["evidenceSha256"] = ReviewAnalysisPolicy.EVIDENCE_SHA256
            });
        stage["errorCode"] = "POLICY_DISABLED";
        return stage;
    }

    private static JsonObject ReportStage(string inputSha256, string outputSha256)
    {
        JsonObject stage = Stage("REPORT",
            "SUCCEEDED",
            inputSha256,
             new JsonObject { ["findings"] = 1, ["findingOverflow"] = 0, ["omissions"] = 0, ["omissionOverflow"] = 0 },
            new JsonObject { ["mode"] = "DISABLED_BY_EVALUATION" });
        stage["outputSha256"] = outputSha256;
        return stage;
    }

    private static JsonObject Stage(
        string stage,
        string outcome,
        string? inputSha256,
        JsonObject counts,
        JsonObject toolIdentity) =>
        new()
        {
            ["stage"] = stage,
            ["applicability"] = "MANDATORY",
            ["outcome"] = outcome,
            ["errorCode"] = null,
            ["retryable"] = false,
            ["durationSeconds"] = 0.25,
            ["inputSha256"] = inputSha256,
            ["outputSha256"] = AdmittedReportTestEvidence.NewHex64(),
            ["toolIdentity"] = toolIdentity,
            ["counts"] = counts
        };

    private static JsonObject Finding(string findingId, string? reason = null) =>
        new()
        {
            ["findingId"] = findingId,
            ["type"] = "SIMILARITY_TOKEN",
            ["path"] = "owned/path.cs",
            ["fileId"] = new string('e', 64),
            ["startLine"] = 1,
            ["endLine"] = 4,
            ["startByte"] = 12,
            ["endByte"] = 38,
            ["ownExcerpt"] = "example",
            ["foreign"] = new JsonObject
            {
                ["fileId"] = new string('f', 64),
                ["sensitivity"] = "PUBLIC",
                ["origin"] = "FOREIGN",
                ["attribution"] = new JsonObject { ["label"] = "public reference" }
            },
            ["channelScore"] = 0.5,
            ["reason"] = reason ?? "Token channel overlap with the pinned reference corpus.",
            ["requestedAction"] = "Review the overlapping code and rewrite it in your own words if it is not yours."
        };
}
