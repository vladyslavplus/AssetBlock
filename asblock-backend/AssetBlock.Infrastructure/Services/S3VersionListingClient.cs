using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Uses the S3 ListObjectVersions marker contract directly. MinIO .NET 7's high-level version
/// enumerator follows ListObjectsV2 continuation tokens, which are not the version-list markers.
/// </summary>
internal sealed class S3VersionListingClient(Uri endpoint, string bucket, string accessKey, string secretKey)
{
    private static readonly HttpClient _httpClient = new();
    private const string REGION = "us-east-1";
    private const string SERVICE = "s3";
    private const string EMPTY_PAYLOAD_HASH = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public async Task<StorageObjectPage> ListPage(
        string? prefix,
        StorageObjectCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        List<KeyValuePair<string, string>> parameters =
        [
            new("versions", string.Empty),
            new("encoding-type", "url"),
            new("max-keys", pageSize.ToString(CultureInfo.InvariantCulture))
        ];
        if (prefix is not null)
        {
            parameters.Add(new("prefix", prefix));
        }
        if (cursor is not null)
        {
            parameters.Add(new("key-marker", cursor.Key));
            if (cursor.VersionId is not null)
            {
                parameters.Add(new("version-id-marker", cursor.VersionId));
            }
        }

        parameters.Sort((left, right) =>
        {
            var key = StringComparer.Ordinal.Compare(Encode(left.Key), Encode(right.Key));
            return key != 0 ? key : StringComparer.Ordinal.Compare(Encode(left.Value), Encode(right.Value));
        });
        var canonicalQuery = string.Join('&', parameters.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        var path = $"/{EncodePath(bucket)}/";
        var requestUri = new Uri(endpoint, path + "?" + canonicalQuery);
        DateTime utcNow = DateTime.UtcNow;
        var date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var host = requestUri.IsDefaultPort ? requestUri.Host : requestUri.Authority;
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{EMPTY_PAYLOAD_HASH}\nx-amz-date:{timestamp}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"GET\n{requestUri.AbsolutePath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{EMPTY_PAYLOAD_HASH}";
        var scope = $"{date}/{REGION}/{SERVICE}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var signingKey = DeriveSigningKey(date);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Host = host;
        request.Headers.Add("x-amz-content-sha256", EMPTY_PAYLOAD_HASH);
        request.Headers.Add("x-amz-date", timestamp);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"S3 ListObjectVersions returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        XDocument document = await XDocument.LoadAsync(responseStream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        XElement root = document.Root ?? throw new InvalidDataException("S3 ListObjectVersions response has no root element.");
        var isTruncated = ReadBool(root, "IsTruncated");
        var items = new List<StorageObjectInfo>();
        foreach (XElement element in root.Elements().Where(e => e.Name.LocalName is "Version" or "DeleteMarker"))
        {
            var key = Decode(ReadText(element, "Key"));
            var versionId = ReadOptionalText(element, "VersionId");
            DateTimeOffset? modified = DateTimeOffset.TryParse(
                ReadOptionalText(element, "LastModified"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
                ? parsed
                : null;
            var size = long.TryParse(ReadOptionalText(element, "Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize
                : 0;
            items.Add(new StorageObjectInfo(key, modified, size, versionId, element.Name.LocalName == "DeleteMarker"));
        }

        if (items.Count > pageSize)
        {
            throw new InvalidDataException("S3 ListObjectVersions exceeded the requested bounded page size.");
        }

        StorageObjectCursor? nextCursor = null;
        if (isTruncated)
        {
            var nextKey = ReadOptionalText(root, "NextKeyMarker");
            var nextVersionId = ReadOptionalText(root, "NextVersionIdMarker");
            if (string.IsNullOrEmpty(nextKey) || items.Count == 0)
            {
                throw new InvalidDataException("S3 ListObjectVersions was truncated without a continuation key marker.");
            }

            var decodedNextKey = Decode(nextKey);
            StorageObjectInfo lastItem = items[^1];
            // The supported pgsty MinIO build appends its internal cache sentinel to
            // NextKeyMarker. The returned Version entries keep the actual key; use the
            // page's final key for that provider response while retaining its version marker.
            if (decodedNextKey.StartsWith(lastItem.Key + "[minio_cache:v2,", StringComparison.Ordinal))
            {
                decodedNextKey = lastItem.Key;
            }

            nextCursor = new StorageObjectCursor(decodedNextKey, nextVersionId);
            if (cursor == nextCursor)
            {
                throw new InvalidDataException("S3 ListObjectVersions returned a non-advancing continuation marker.");
            }
        }

        return new StorageObjectPage(items, nextCursor, !isTruncated);
    }

    private byte[] DeriveSigningKey(string date)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(REGION));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(SERVICE));
        return HMACSHA256.HashData(serviceKey, "aws4_request"u8);
    }

    private static string ReadText(XElement element, string name) =>
        ReadOptionalText(element, name) ?? throw new InvalidDataException($"S3 ListObjectVersions response is missing {name}.");

    private static string? ReadOptionalText(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    private static bool ReadBool(XElement element, string name) =>
        bool.TryParse(ReadOptionalText(element, name), out var value) && value;

    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static string EncodePath(string value) =>
        string.Join('/', value.Split('/').Select(Encode));

    private static string Encode(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
