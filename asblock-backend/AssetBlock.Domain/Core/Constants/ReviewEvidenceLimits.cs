namespace AssetBlock.Domain.Core.Constants;

public static class ReviewEvidenceLimits
{
    public const int MAX_TEXT_BYTES = 4096;
    public const int MAX_RESPONSE_BYTES = 65536;
    public const int MAX_ITEMS = 20;
    public const int TEXT_LENGTH = 2000;
    public const int PLAN_REFERENCE_LENGTH = 200;
    public const int PAGE_SIZE = 20;
    public const string TRUNCATION_MARKER = "\n[TRUNCATED]";
}
