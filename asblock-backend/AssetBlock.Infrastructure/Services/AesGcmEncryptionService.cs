using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AesGcmEncryptionService(IOptions<EncryptionOptions> options) : IEncryptionService
{
    private const int NONCE_SIZE = 12;
    private const int TAG_SIZE = 16;
    private static readonly int[] _validKeyLengths = [16, 24, 32];
    private byte[]? _cachedKey;

    public async Task<byte[]> Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var plainBytes = await ReadFully(plain, cancellationToken);
        var nonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TAG_SIZE];

        using var aes = new AesGcm(key, TAG_SIZE);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        await cipher.WriteAsync(nonce, cancellationToken);
        await cipher.WriteAsync(cipherBytes, cancellationToken);
        await cipher.WriteAsync(tag, cancellationToken);
        await cipher.FlushAsync(cancellationToken);

        return nonce;
    }

    public async Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var cipherBytes = await ReadFully(cipher, cancellationToken);
        if (cipherBytes.Length < NONCE_SIZE + TAG_SIZE)
        {
            throw new CryptographicException("Cipher stream too short.");
        }

        var nonce = cipherBytes.AsSpan(0, NONCE_SIZE);
        var encryptedLength = cipherBytes.Length - NONCE_SIZE - TAG_SIZE;
        var tag = cipherBytes.AsSpan(cipherBytes.Length - TAG_SIZE, TAG_SIZE);

        var plainBytes = new byte[encryptedLength];
        using var aes = new AesGcm(key, TAG_SIZE);
        aes.Decrypt(nonce, cipherBytes.AsSpan(NONCE_SIZE, encryptedLength), tag, plainBytes);

        await plain.WriteAsync(plainBytes, cancellationToken);
        await plain.FlushAsync(cancellationToken);
    }

    private byte[] GetKey()
    {
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }
        var keyBase64 = options.Value.KeyBase64;
        if (string.IsNullOrEmpty(keyBase64))
        {
            throw new InvalidOperationException("Encryption:KeyBase64 is not configured.");
        }
        var key = Convert.FromBase64String(keyBase64);
        if (Array.IndexOf(_validKeyLengths, key.Length) < 0)
        {
            throw new InvalidOperationException($"Encryption key must be 16, 24, or 32 bytes. Got {key.Length} bytes.");
        }
        _cachedKey = key;
        return key;
    }

    /// <summary>Loads full stream into memory. For large files, consider streaming/chunked encryption to reduce memory pressure.</summary>
    private static async Task<byte[]> ReadFully(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }
}
