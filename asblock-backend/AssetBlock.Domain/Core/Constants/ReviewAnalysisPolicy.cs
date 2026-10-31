namespace AssetBlock.Domain.Core.Constants;

/// <summary>Immutable product policy enabling eligibility for an explicit human decision.</summary>
public static class ReviewAnalysisPolicy
{
    public const string KEY = "code-analysis-policy-v1";
    public const string DIGEST = "0b283e5d9d81abc5c7a1ce3cbbc68b6ddc7e90f911920f833ba13f00f8269e15";
    public const int MAX_QUERIES = 2000;
    public const int TOKEN_TOP = 20;
    public const int SEMANTIC_TOP = 20;
    public const int UNION_BUDGET = 40;
    public const string VERIFIER_MODE = "DISABLED_BY_EVALUATION";
    public const string VERIFIER_REASON = "Pair verification is disabled by evaluation; retained comparison evidence requires an explicit human decision.";
    // Owned by the frozen pair-verification evidence package. This is evidence, never a model activation.
    public const string EVIDENCE_SHA256 = "676ad4da5a40ea7a6f040b0c51cc060088689cfd26f23d4a3974b965f1063973";
}
