namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record PublishAssetVersionRequest(
    string LicenseCode,
    string ReleaseNotes);
