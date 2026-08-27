namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Constants for tag validation and format limits.
/// </summary>
public static class TagConstants
{
    public const int NAME_MAX_LENGTH = ListingSuggestionBounds.TAG_NAME_MAX_LENGTH;
    public const string SLUG_PATTERN = "^[a-z0-9]+(-[a-z0-9]+)*$";
}
