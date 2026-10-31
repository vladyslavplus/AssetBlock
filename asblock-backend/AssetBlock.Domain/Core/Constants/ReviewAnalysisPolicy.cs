namespace AssetBlock.Domain.Core.Constants;

/// <summary>Immutable product policy enabling eligibility for an explicit human decision.</summary>
public static class ReviewAnalysisPolicy
{
    public const string KEY = "code-analysis-policy-v1";
    public const string DIGEST = "10ee71111355f6bfb13cef99fd019ffccf0328da0c829bf293bd1d8c3f1ff022";
    public const string VERIFIER_MODE = "DISABLED_BY_EVALUATION";
    public const string VERIFIER_REASON = "Pair verification is disabled by evaluation; retained comparison evidence requires an explicit human decision.";
    // Owned by the frozen pair-verification evidence package. This is evidence, never a model activation.
    public const string EVIDENCE_SHA256 = "676ad4da5a40ea7a6f040b0c51cc060088689cfd26f23d4a3974b965f1063973";
}
