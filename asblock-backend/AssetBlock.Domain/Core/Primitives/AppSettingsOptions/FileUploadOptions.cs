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
        ".7z",
        ".rar",
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
}
