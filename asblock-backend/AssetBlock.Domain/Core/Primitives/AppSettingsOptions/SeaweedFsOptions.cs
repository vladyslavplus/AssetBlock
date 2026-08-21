namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class SeaweedFsOptions
{
    public const string SECTION_NAME = "SeaweedFs";

    /// <summary>S3-compatible API endpoint as host:port (e.g. localhost:8333) or absolute http(s) URI without path/query.</summary>
    public string Endpoint { get; set; } = "localhost:8333";
    public string Bucket { get; set; } = "assets";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
}
