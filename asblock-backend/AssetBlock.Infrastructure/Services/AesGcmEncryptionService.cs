using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AesGcmEncryptionService(IOptions<EncryptionOptions> options) : IEncryptionService
{
    private const int NONCE_SIZE = 12;
    private const int TAG_SIZE = 16;

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

        var nonce = cipherBytes.AsSpan(0, NONCE_SIZE).ToArray();
        var tag = cipherBytes.AsSpan(cipherBytes.Length - TAG_SIZE, TAG_SIZE).ToArray();
        var encrypted = cipherBytes.AsSpan(NONCE_SIZE, cipherBytes.Length - NONCE_SIZE - TAG_SIZE);

        var plainBytes = new byte[encrypted.Length];
        using var aes = new AesGcm(key, TAG_SIZE);
        aes.Decrypt(nonce, encrypted.ToArray(), tag, plainBytes);

        await plain.WriteAsync(plainBytes, cancellationToken);
        await plain.FlushAsync(cancellationToken);
    }

    private byte[] GetKey()
    {
        var keyBase64 = options.Value.KeyBase64;
        return string.IsNullOrEmpty(keyBase64) ? throw new InvalidOperationException("Encryption:KeyBase64 is not configured.") : Convert.FromBase64String(keyBase64);
    }

    private static async Task<byte[]> ReadFully(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }
}
