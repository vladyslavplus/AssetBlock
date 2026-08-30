namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class FileUploadOptions
{
    public const string SECTION_NAME = "FileUpload";

    /// <summary>Maximum accepted upload size (250 MiB).</summary>
    public long MaxFileBytes { get; set; } = 250L * 1024 * 1024;

    /// <summary>
    /// Allowed archive suffixes. Multi-part suffixes (e.g. .tar.gz) must be listed and are matched
    /// before shorter ones.
    /// </summary>
    public string[] AllowedExtensions { get; set; } =
    [
        ".zip",
        ".tar",
        ".tar.gz",
        ".tgz"
    ];

    /// <summary>
    /// Matches the longest allowed suffix (case-insensitive). Prefer <c>.tar.gz</c> over <c>.gz</c>.
    /// </summary>
    public bool TryMatchAllowedExtension(string fileName, out string matchedExtension)
    {
        matchedExtension = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        string? best = null;
        foreach (var ext in AllowedExtensions)
        {
            if (string.IsNullOrWhiteSpace(ext))
            {
                continue;
            }

            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || ext.Length > best.Length)
            {
                best = ext;
            }
        }

        if (best is null)
        {
            return false;
        }

        matchedExtension = best.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Normalizes a client-supplied filename into a safe, conservative ASCII display filename.
    /// Strips directory paths, non-ASCII/control/header/quote characters, preserves matched allowed extension,
    /// falls back to a deterministic safe name, and caps base length.
    /// </summary>
    public string NormalizeDisplayFileName(string? rawFileName, string fallbackBaseName = "asset")
    {
        var safeFallback = string.IsNullOrWhiteSpace(fallbackBaseName) ? "asset" : fallbackBaseName.Trim();
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return $"{safeFallback}.zip";
        }

        // 1. Strip path components
        var stripped = rawFileName.Trim().Replace('\\', '/');
        var slashIdx = stripped.LastIndexOf('/');
        if (slashIdx >= 0)
        {
            stripped = stripped[(slashIdx + 1)..];
        }

        if (string.IsNullOrWhiteSpace(stripped))
        {
            return $"{safeFallback}.zip";
        }

        // 2. Match allowed extension
        var hasAllowedExt = TryMatchAllowedExtension(stripped, out var matchedExt);
        var ext = hasAllowedExt ? matchedExt : ".zip";
        var baseName = hasAllowedExt && stripped.Length > ext.Length
            ? stripped[..^ext.Length]
            : stripped;

        // 3. Normalize base name to conservative ASCII [a-zA-Z0-9._-]
        var sb = new System.Text.StringBuilder(baseName.Length);
        foreach (var c in baseName)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('_');
            }
        }

        var cleaned = sb.ToString().Trim('.', '_', '-');

        // Collapse multiple dots to prevent traversal/hidden file semantics
        while (cleaned.Contains(".."))
        {
            cleaned = cleaned.Replace("..", ".");
        }

        // 4. Fallback if empty or all invalid
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = safeFallback;
        }

        // 5. Cap base name length (max 100 chars)
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[..100].TrimEnd('.', '_', '-');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = safeFallback;
            }
        }

        // 6. Defensively ensure matched extension contains only safe ASCII alphanumeric and dot characters
        var extSb = new System.Text.StringBuilder(ext.Length);
        foreach (var c in ext)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.')
            {
                extSb.Append(char.ToLowerInvariant(c));
            }
        }
        var safeExt = extSb.ToString();
        if (string.IsNullOrWhiteSpace(safeExt) || !safeExt.StartsWith('.'))
        {
            safeExt = ".zip";
        }

        return $"{cleaned}{safeExt}";
    }
}
