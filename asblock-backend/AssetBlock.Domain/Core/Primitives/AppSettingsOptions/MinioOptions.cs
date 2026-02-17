namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class MinioOptions
{
    public const string SECTION_NAME = "Minio";

    public string Endpoint { get; set; } = "localhost:9000";
    public string Bucket { get; set; } = "assets";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
}
