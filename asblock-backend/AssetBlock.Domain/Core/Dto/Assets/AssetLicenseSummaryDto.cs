namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record AssetLicenseSummaryDto(
    string Code,
    string DisplayName,
    string TemplateVersion,
    string Terms);
