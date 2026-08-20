using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Licenses;

/// <summary>Immutable platform license template metadata and terms.</summary>
public sealed record AssetLicenseTemplate(
    AssetLicenseCode Code,
    string DisplayName,
    string TemplateVersion,
    string TermsPlainText);
