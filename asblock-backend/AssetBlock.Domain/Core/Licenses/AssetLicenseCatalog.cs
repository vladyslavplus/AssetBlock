using System.Net;
using System.Text;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Licenses;

/// <summary>
/// Deterministic catalog of platform-owned license templates.
/// Template text is code-owned; sold versions snapshot these fields at publish time.
/// </summary>
public static class AssetLicenseCatalog
{
    private const string CURRENT_TEMPLATE_VERSION = "1.0";

    private static readonly IReadOnlyDictionary<AssetLicenseCode, AssetLicenseTemplate> _templates =
        new Dictionary<AssetLicenseCode, AssetLicenseTemplate>
        {
            [AssetLicenseCode.PERSONAL] = new(
                AssetLicenseCode.PERSONAL,
                "Personal use",
                CURRENT_TEMPLATE_VERSION,
                """
                AssetBlock Personal Use License (template 1.0)

                This license grants the purchaser a non-exclusive, non-transferable right to use the
                purchased digital asset for personal, non-commercial projects only.

                You may download and use the purchased version (and later entitled versions of the
                same asset) for learning, personal prototypes, and private projects that are not
                sold, licensed, or publicly offered as a product or service.

                You may not: redistribute the asset as-is; resell or sublicense the files; claim
                authorship of the original asset; or use the asset as the primary deliverable of a
                paid commercial product without a Commercial license.

                This text is a platform product license summary, not legal advice.
                """.Trim()),
            [AssetLicenseCode.COMMERCIAL] = new(
                AssetLicenseCode.COMMERCIAL,
                "Commercial use",
                CURRENT_TEMPLATE_VERSION,
                """
                AssetBlock Commercial Use License (template 1.0)

                This license grants the purchaser a non-exclusive, non-transferable right to use the
                purchased digital asset in commercial products and client work.

                You may incorporate the purchased version (and later entitled versions of the same
                asset) into applications, templates, and services you sell or deliver to clients,
                provided the asset is not redistributed as a standalone competing marketplace item.

                You may not: resell or redistribute the raw asset files as a separate product on a
                marketplace; transfer this license to another party; or remove attribution
                requirements that were part of the asset packaging when present.

                This text is a platform product license summary, not legal advice.
                """.Trim())
        };

    public static IReadOnlyCollection<AssetLicenseTemplate> All => _templates.Values.ToArray();

    public static bool TryGet(AssetLicenseCode code, out AssetLicenseTemplate template) =>
        _templates.TryGetValue(code, out template!);

    public static AssetLicenseTemplate Get(AssetLicenseCode code) =>
        _templates.TryGetValue(code, out var template)
            ? template
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown license code.");

    public static bool TryParseCode(string? raw, out AssetLicenseCode code)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            code = default;
            return false;
        }

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out code)
               && Enum.IsDefined(code)
               && _templates.ContainsKey(code);
    }

    /// <summary>HTML built from encoded paragraphs — not stored raw HTML and not author-supplied.</summary>
    public static string ToSafeHtml(AssetLicenseTemplate template)
    {
        var paragraphs = template.TermsPlainText
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        sb.Append("<div class=\"asset-license-terms\">");
        foreach (var paragraph in paragraphs)
        {
            sb.Append("<p>");
            sb.Append(WebUtility.HtmlEncode(paragraph).Replace("\n", "<br/>", StringComparison.Ordinal));
            sb.Append("</p>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }
}
