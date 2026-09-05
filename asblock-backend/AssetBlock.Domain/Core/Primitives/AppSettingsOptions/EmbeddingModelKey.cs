using System.Security.Cryptography;
using System.Text;

namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public static class EmbeddingModelKey
{
    public static string Compute(
        string provider,
        string model,
        string revision,
        string digest,
        int dimension,
        string contentSchemaVersion)
    {
        var raw = $"{provider}\0{model}\0{revision}\0{digest}\0{dimension}\0{contentSchemaVersion}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Compute(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Compute(
            options.Provider,
            options.Model,
            options.Revision,
            options.Digest,
            options.Dimension,
            options.ContentSchemaVersion);
    }
}
