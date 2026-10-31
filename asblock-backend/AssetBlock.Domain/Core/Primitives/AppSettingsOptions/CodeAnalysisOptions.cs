namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>
/// Configuration of the external code analysis service and of the shared capacity that bounds how
/// many analyses may execute against it at once.
/// </summary>
public sealed class CodeAnalysisOptions
{
    public const string SECTION_NAME = "CodeAnalysis";

    public const int MIN_CAPACITY = 1;
    public const int MAX_CAPACITY = 64;
    public const int MIN_JOB_DEFINITION_VERSION = 1;
    public const int MAX_JOB_DEFINITION_VERSION = 1000;

    public bool Enabled { get; init; }

    /// <summary>Stable shared service identity. One key per actual analysis service instance.</summary>
    public string ServiceKey { get; init; } = "code-analysis";

    /// <summary>Number of concurrently executable slots of that shared service.</summary>
    public int Capacity { get; init; } = 1;

    /// <summary>Job definition version for bound analysis jobs. Never used as a retry counter.</summary>
    public int JobDefinitionVersion { get; init; } = 1;

    /// <summary>Absolute base address of the analysis service.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>Server-side shared secret presented to the analysis service.</summary>
    public string ServiceSecret { get; init; } = "";

    /// <summary>Header the analysis service expects the shared secret in.</summary>
    public string AuthenticationHeader { get; init; } = "X-Analysis-Secret";

    /// <summary>Pinned policy identity this deployment admits. No policy row means no analysis.</summary>
    public string PolicyKey { get; init; } = Constants.ReviewAnalysisPolicy.KEY;

    /// <summary>
    /// Digest of the pinned policy manifest. Resolved together with the key so a deployment never
    /// silently picks up a different policy revision that happens to share the key.
    /// </summary>
    public string PolicyDigest { get; init; } = "";

    /// <summary>
    /// Digest over the runtime manifest the deployment approves: the report contract, stage matrix,
    /// observed tool/parser/runtime identities, verifier mode, operation limits and security
    /// validity window. Readiness recomputes it from the persisted pinned row, so a policy that
    /// only happens to share the key and policy digest still cannot authorize a claim. Blank keeps
    /// every new analysis claim closed.
    /// </summary>
    public string RuntimeManifestDigest { get; init; } = "";

    /// <summary>
    /// The approved policy manifest this deployment publishes at startup. It is the single source
    /// of the row that readiness later proves; leaving it incomplete keeps analysis closed rather
    /// than publishing a partial identity.
    /// </summary>
    public AnalysisPolicyManifestOptions PolicyManifest { get; init; } = new();

    /// <summary>Immutable operation budget for one analysis, independent of other job types.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Execution deadline the service is asked to enforce for one request.</summary>
    public TimeSpan ServiceDeadline { get; init; } = TimeSpan.FromMinutes(18);

    /// <summary>Immutable hard deadline for one reserved execution; renewal never extends it.</summary>
    public TimeSpan ReservationHardDeadline { get; init; } = TimeSpan.FromMinutes(18);

    /// <summary>Bounded grace after the hard deadline before a reservation may be reassigned.</summary>
    public TimeSpan ReservationCleanupGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum accepted analysis response size.</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Maximum metadata document sent ahead of the archive stream.</summary>
    public int MaxMetadataBytes { get; init; } = 64 * 1024;
}

/// <summary>
/// The approved analysis policy manifest declared by a deployment. Every member is published
/// verbatim into one immutable <c>code_analysis_policy_versions</c> row, so a blank member keeps the
/// whole publication rejected instead of admitting an identity that was never approved.
/// </summary>
public sealed class AnalysisPolicyManifestOptions
{
    public string PolicyKey { get; init; } = "";

    /// <summary>Digest the analysis service assigns to this policy manifest.</summary>
    public string PolicyDigest { get; init; } = "";

    /// <summary>Report contract identity, normally the shipped analysis schema name.</summary>
    public string SchemaName { get; init; } = "";

    /// <summary>Mandatory/optional stage matrix, as a JSON object.</summary>
    public string StageMatrixJson { get; init; } = "";

    public string VerifierMode { get; init; } = "";

    public string VerifierReason { get; init; } = "";

    /// <summary>Observed tool, parser and runtime identities, as a JSON object.</summary>
    public string ToolIdentitiesJson { get; init; } = "";

    /// <summary>Immutable operation budget for one analysis, as a JSON object.</summary>
    public string LimitsJson { get; init; } = "";

    /// <summary>Security validity window the analysis must satisfy, as a JSON object.</summary>
    public string SecurityValidityJson { get; init; } = "";

    public string? ModelKey { get; init; }

    public string? IndexKey { get; init; }

    public string? ReferenceSnapshotId { get; init; }
}
