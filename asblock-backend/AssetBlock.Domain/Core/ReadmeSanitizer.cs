using System.Text;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Domain.Core;

/// <summary>
/// Removes privacy-sensitive and prompt-injection-prone material from README content before
/// it is included in an AI generation request.
///
/// <para>Guarantees (in order):</para>
/// <list type="bullet">
///   <item>CRLF and CR line endings are normalized to LF.</item>
///   <item>Fenced code blocks (<c>```…```</c> and <c>~~~…~~~</c>) are removed line-by-line. Unclosed opening fences fail closed and drop the remainder of the document.</item>
///   <item>Indented code lines (4+ spaces or tab-prefixed) are removed.</item>
///   <item>HTTP/HTTPS/FTP/SSH URLs and bare www. references are removed.</item>
///   <item>E-mail addresses are removed.</item>
///   <item>Lines containing credential-pattern keywords are removed in their entirety.</item>
///   <item>Consecutive blank lines are collapsed to one.</item>
///   <item>Output is trimmed and capped at <see cref="ListingSuggestionBounds.README_TEXT_MAX_CHARS"/>.</item>
///   <item>An empty or whitespace-only result returns <c>null</c>.</item>
/// </list>
/// </summary>
public static partial class ReadmeSanitizer
{
    // URL patterns
    [GeneratedRegex(@"(?:https?|ftp|ssh)://\S+|www\.\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    // Email addresses
    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    // Credential-pattern keywords (case-insensitive alphanumeric boundary check)
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:password|secret|token|api[\s\-_]?key|private[\s\-_]?key|licen[sc]e[\s\-_]?key|access[\s\-_]?key|bearer[\s\-_]?token|auth[\s\-_]?token)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CredentialKeywordRegex();

    /// <summary>
    /// Sanitizes <paramref name="raw"/> README text and returns the cleaned excerpt,
    /// or <c>null</c> when the result is empty after redaction.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 1. Normalize line endings to LF
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var cleanedLines = new List<string>(lines.Length);
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        foreach (var rawLine in lines)
        {
            if (!inFence)
            {
                if (TryParseOpeningFence(rawLine, out var detectedChar, out var detectedLen))
                {
                    inFence = true;
                    fenceChar = detectedChar;
                    fenceLength = detectedLen;
                    continue; // Skip opening fence line
                }
            }
            else
            {
                if (IsClosingFence(rawLine, fenceChar, fenceLength))
                {
                    inFence = false;
                    fenceChar = '\0';
                    fenceLength = 0;
                }

                // Still inside fence -> drop line (fail-closed if unclosed)
                continue;
            }

            // 2. Check for indented code (4+ spaces or starts with tab)
            if (rawLine.StartsWith("    ", StringComparison.Ordinal) || rawLine.StartsWith('\t'))
            {
                continue;
            }

            // 3. Drop entire line if it matches credential patterns
            if (CredentialKeywordRegex().IsMatch(rawLine))
            {
                continue;
            }

            // 4. Strip URLs and Emails from line
            var line = UrlRegex().Replace(rawLine, string.Empty);
            line = EmailRegex().Replace(line, string.Empty);

            cleanedLines.Add(line.TrimEnd());
        }

        // 5. Reassemble and collapse multiple blank lines
        var sb = new StringBuilder();
        var previousWasBlank = true; // Avoid leading blank lines

        foreach (var line in cleanedLines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank)
            {
                if (!previousWasBlank)
                {
                    sb.Append('\n');
                    previousWasBlank = true;
                }
            }
            else
            {
                if (sb.Length > 0 && !previousWasBlank)
                {
                    sb.Append('\n');
                }
                sb.Append(line);
                previousWasBlank = false;
            }
        }

        var result = sb.ToString().Trim();

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        // 6. Enforce hard length cap using UTF-16 char count consistent with the domain bound.
        if (result.Length > ListingSuggestionBounds.README_TEXT_MAX_CHARS)
        {
            result = TruncateAtLineBoundary(result, ListingSuggestionBounds.README_TEXT_MAX_CHARS);
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool TryParseOpeningFence(string line, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        var trimmedStart = line.TrimStart(' ');
        var leadingSpaces = line.Length - trimmedStart.Length;
        if (leadingSpaces > 3 || trimmedStart.Length < 3)
        {
            return false;
        }

        var first = trimmedStart[0];
        if (first != '`' && first != '~')
        {
            return false;
        }

        var count = 0;
        while (count < trimmedStart.Length && trimmedStart[count] == first)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        // For backtick fences, inline backticks are not allowed in info string
        if (first == '`' && trimmedStart[count..].Contains('`'))
        {
            return false;
        }

        fenceChar = first;
        fenceLength = count;
        return true;
    }

    private static bool IsClosingFence(string line, char openFenceChar, int openFenceLength)
    {
        var trimmedStart = line.TrimStart(' ');
        var leadingSpaces = line.Length - trimmedStart.Length;
        if (leadingSpaces > 3 || trimmedStart.Length < openFenceLength)
        {
            return false;
        }

        var first = trimmedStart[0];
        if (first != openFenceChar)
        {
            return false;
        }

        var count = 0;
        while (count < trimmedStart.Length && trimmedStart[count] == openFenceChar)
        {
            count++;
        }

        if (count < openFenceLength)
        {
            return false;
        }

        // Closing fence allows ONLY trailing spaces or tabs after the fence marker
        for (var i = count; i < trimmedStart.Length; i++)
        {
            var c = trimmedStart[i];
            if (c != ' ' && c != '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static string TruncateAtLineBoundary(string text, int maxChars)
    {
        var truncated = text[..maxChars];
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > maxChars / 2)
        {
            truncated = truncated[..lastNewline];
        }

        return truncated.TrimEnd();
    }
}
