using System.Text;

namespace AssetBlock.Application.Common;

/// <summary>
/// Bounded and deterministic catalog search query normalization and validation.
/// </summary>
public static class CatalogSearchNormalization
{
    public const int MAX_UNICODE_SCALARS = 256;

    /// <summary>
    /// Validates that the search query contains at most 256 Unicode scalar values.
    /// </summary>
    public static bool BeWithinUnicodeScalarLimit(string? search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        return CountUnicodeScalars(search) <= MAX_UNICODE_SCALARS;
    }

    /// <summary>
    /// Validates that the search query does not contain invalid control characters.
    /// Standard whitespace control characters (\t, \r, \n) are allowed and normalized;
    /// all other control characters (e.g. NUL, BEL, ESC, C0/C1 controls) are rejected.
    /// </summary>
    public static bool NotContainInvalidControlCharacters(string? search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        foreach (Rune rune in search.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value != '\t' && rune.Value != '\r' && rune.Value != '\n')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes search input: Unicode NFKC, trims outer whitespace, collapses repeated whitespace
    /// into a single space, and bounds length to 256 Unicode scalars.
    /// Returns null when the resulting string is empty.
    /// </summary>
    public static string? NormalizeSearchQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        // 1. Unicode NFKC normalization
        var nfkc = search.Normalize(NormalizationForm.FormKC);

        // 2. Collapse whitespace (spaces, tabs, newlines) into single space
        var builder = new StringBuilder(nfkc.Length);
        var inWhitespace = false;
        var trimmedStart = false;

        foreach (Rune rune in nfkc.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (trimmedStart && !inWhitespace)
                {
                    inWhitespace = true;
                }
            }
            else
            {
                if (inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = false;
                }
                builder.Append(rune.ToString());
                trimmedStart = true;
            }
        }

        var result = builder.ToString();
        if (result.Length == 0)
        {
            return null;
        }

        // 3. Bound to 256 Unicode scalar values at scalar boundary
        if (CountUnicodeScalars(result) > MAX_UNICODE_SCALARS)
        {
            result = TruncateToUnicodeScalars(result, MAX_UNICODE_SCALARS);
        }

        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// Counts Unicode scalar values (Runes) in the given text.
    /// </summary>
    public static int CountUnicodeScalars(string text)
    {
        var count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Truncates text to at most maxScalars Unicode scalars, guaranteeing safe scalar boundary.
    /// </summary>
    private static string TruncateToUnicodeScalars(string text, int maxScalars)
    {
        if (maxScalars <= 0)
        {
            return string.Empty;
        }

        var count = 0;
        var charLength = 0;

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (count >= maxScalars)
            {
                break;
            }
            count++;
            charLength += rune.Utf16SequenceLength;
        }

        return text[..charLength];
    }
}
