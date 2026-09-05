using System.Security.Cryptography;
using System.Text;

namespace AssetBlock.Application.Common;

/// <summary>
/// Result of public listing metadata canonicalization.
/// </summary>
public sealed record CanonicalPublicMetadataResult(
    string CanonicalText,
    string ContentHash,
    bool IsTruncated);

/// <summary>
/// Canonical public metadata v1 builder and hasher (asset-public-metadata-v1).
/// Guarantees safe embedding content: includes only title, description, category name,
/// and sorted distinct public tag names. Explicitly excludes README, archive, filenames,
/// paths, seller identity, private content, prompts, errors, and storage data.
/// </summary>
public static class AssetPublicMetadataCanonicalizer
{
    public const string CONTENT_SCHEMA_VERSION = "asset-public-metadata-v1";

    public const int TITLE_MAX_CHARS = 500;
    public const int DESCRIPTION_MAX_CHARS = 5000;
    public const int CATEGORY_MAX_CHARS = 200;
    public const int TAG_MAX_CHARS = 50;
    public const int TAG_MAX_COUNT = 50;

    public const int MAX_COMPOSED_CHARS = 8192;
    public const int MAX_COMPOSED_UTF8_BYTES = 32768; // 32 KiB

    /// <summary>
    /// Produces deterministic canonical text and lowercase SHA-256 hash for public listing metadata.
    /// </summary>
    public static CanonicalPublicMetadataResult Canonicalize(
        string? title,
        string? description,
        string? categoryName,
        IEnumerable<string>? tags)
    {
        var truncated = false;

        // 1. Normalize title (required, max 500 chars)
        var normTitle = NormalizeSingleLineField(title ?? string.Empty);
        if (normTitle.Length > TITLE_MAX_CHARS)
        {
            normTitle = TruncateUtf16WithoutBreakingSurrogates(normTitle, TITLE_MAX_CHARS);
            truncated = true;
        }

        // 2. Normalize description (multi-line, max 5000 chars)
        var normDesc = NormalizeMultiLineField(description);
        if (!string.IsNullOrEmpty(normDesc) && normDesc.Length > DESCRIPTION_MAX_CHARS)
        {
            normDesc = TruncateUtf16WithoutBreakingSurrogates(normDesc, DESCRIPTION_MAX_CHARS);
            truncated = true;
        }

        // 3. Normalize category name (single-line, max 200 chars)
        var normCat = NormalizeSingleLineField(categoryName);
        if (!string.IsNullOrEmpty(normCat) && normCat.Length > CATEGORY_MAX_CHARS)
        {
            normCat = TruncateUtf16WithoutBreakingSurrogates(normCat, CATEGORY_MAX_CHARS);
            truncated = true;
        }

        // 4. Normalize and sort distinct tags (max 50 tags, each max 50 chars)
        var normTags = new List<string>();
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                var cleanedTag = NormalizeSingleLineField(tag);
                if (string.IsNullOrEmpty(cleanedTag))
                {
                    continue;
                }

                if (cleanedTag.Length > TAG_MAX_CHARS)
                {
                    cleanedTag = TruncateUtf16WithoutBreakingSurrogates(cleanedTag, TAG_MAX_CHARS);
                    truncated = true;
                }

                normTags.Add(cleanedTag);
            }
        }

        // Sort by ordinal normalized name and distinct
        normTags = normTags
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(TAG_MAX_COUNT)
            .ToList();

        // 5. Compose with fixed field labels and order
        var sb = new StringBuilder();
        sb.Append("title: ").Append(normTitle);

        if (!string.IsNullOrEmpty(normDesc))
        {
            sb.Append("\ndescription: ").Append(normDesc);
        }

        if (!string.IsNullOrEmpty(normCat))
        {
            sb.Append("\ncategory: ").Append(normCat);
        }

        if (normTags.Count > 0)
        {
            sb.Append("\ntags: ").Append(string.Join(", ", normTags));
        }

        var composed = sb.ToString();

        // 6. Apply composed bounds: 8,192 characters and 32 KiB UTF-8, truncated strictly at Unicode scalar boundary
        (string Text, bool Truncated) boundResult = EnforceComposedBounds(composed);
        if (boundResult.Truncated)
        {
            truncated = true;
        }

        var finalText = boundResult.Text;

        // 7. Compute deterministic lowercase SHA-256 hash of UTF-8 canonical text
        var utf8Bytes = Encoding.UTF8.GetBytes(finalText);
        var hashBytes = SHA256.HashData(utf8Bytes);
        var contentHash = Convert.ToHexStringLower(hashBytes);

        return new CanonicalPublicMetadataResult(finalText, contentHash, truncated);
    }

    private static string NormalizeSingleLineField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Unicode NFKC normalization
        var nfkc = text.Normalize(NormalizationForm.FormKC);

        // Convert CRLF/CR to space for single line fields, collapse repeated whitespace
        var builder = new StringBuilder(nfkc.Length);
        var inWhitespace = false;
        var hasContent = false;

        foreach (Rune rune in nfkc.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (hasContent && !inWhitespace)
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
                hasContent = true;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeMultiLineField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 1. Unicode NFKC normalization
        var nfkc = text.Normalize(NormalizationForm.FormKC);

        // 2. CRLF and CR to LF
        var lfText = nfkc.Replace("\r\n", "\n").Replace('\r', '\n');

        // 3. Trim outer whitespace, collapse horizontal whitespace on each line
        var lines = lfText.Split('\n');
        var processedLines = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var lineBuilder = new StringBuilder(line.Length);
            var inWhitespace = false;
            var trimmedStart = false;

            foreach (Rune rune in line.EnumerateRunes())
            {
                // Horizontal whitespace collapsing (spaces, tabs)
                if (rune.Value is ' ' or '\t' || (Rune.IsWhiteSpace(rune) && rune.Value != '\n'))
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
                        lineBuilder.Append(' ');
                        inWhitespace = false;
                    }
                    lineBuilder.Append(rune.ToString());
                    trimmedStart = true;
                }
            }

            processedLines.Add(lineBuilder.ToString());
        }

        // Collapse 3+ consecutive empty lines to at most 2, and trim trailing/leading empty lines
        var firstNonEmpty = 0;
        while (firstNonEmpty < processedLines.Count && processedLines[firstNonEmpty].Length == 0)
        {
            firstNonEmpty++;
        }

        var lastNonEmpty = processedLines.Count - 1;
        while (lastNonEmpty >= firstNonEmpty && processedLines[lastNonEmpty].Length == 0)
        {
            lastNonEmpty--;
        }

        if (firstNonEmpty > lastNonEmpty)
        {
            return string.Empty;
        }

        var resultBuilder = new StringBuilder();
        var emptyCount = 0;

        for (var i = firstNonEmpty; i <= lastNonEmpty; i++)
        {
            var currentLine = processedLines[i];
            if (currentLine.Length == 0)
            {
                emptyCount++;
                if (emptyCount > 1)
                {
                    continue; // allow at most 1 empty line between content lines
                }
            }
            else
            {
                emptyCount = 0;
            }

            if (resultBuilder.Length > 0)
            {
                resultBuilder.Append('\n');
            }
            resultBuilder.Append(currentLine);
        }

        return resultBuilder.ToString();
    }

    private static (string Text, bool Truncated) EnforceComposedBounds(string text)
    {
        var charCount = 0;
        var utf8ByteCount = 0;
        var charLength = 0;
        var truncated = false;

        foreach (Rune rune in text.EnumerateRunes())
        {
            var runeChars = rune.Utf16SequenceLength;
            var runeUtf8Bytes = rune.Utf8SequenceLength;

            if (charCount + runeChars > MAX_COMPOSED_CHARS || utf8ByteCount + runeUtf8Bytes > MAX_COMPOSED_UTF8_BYTES)
            {
                truncated = true;
                break;
            }

            charCount += runeChars;
            utf8ByteCount += runeUtf8Bytes;
            charLength += runeChars;
        }

        return (text[..charLength], truncated);
    }

    private static string TruncateUtf16WithoutBreakingSurrogates(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        // If the character immediately preceding the cutoff is a high surrogate,
        // cutting at maxChars would orphan it without its low surrogate.
        // Step back by one char to keep surrogate pairs intact.
        var cutLength = maxChars;
        if (char.IsHighSurrogate(text[cutLength - 1]))
        {
            cutLength--;
        }

        return text[..cutLength];
    }
}
