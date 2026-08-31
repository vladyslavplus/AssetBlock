using Minio;

namespace AssetBlock.Infrastructure.Services;

/// <summary>Builds a MinIO .NET SDK client against an S3-compatible endpoint.</summary>
internal static class S3CompatibleClientFactory
{
    public static IMinioClient Create(string endpoint, string accessKey, string secretKey, bool useSsl)
    {
        var builder = new MinioClient();
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return builder
                .WithEndpoint(uri.Host, uri.Port)
                .WithCredentials(accessKey, secretKey)
                .WithSSL(useSsl)
                .Build();
        }

        return builder
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(useSsl)
            .Build();
    }
}
