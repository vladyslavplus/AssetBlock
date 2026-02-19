using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AesGcmEncryptionService(IOptions<EncryptionOptions> options) : IEncryptionService
{
    private const int KEY_SIZE = 32;
    private const int NONCE_SIZE = 12;
    private const int TAG_SIZE = 16;
    private const int CHUNK_SIZE = 1024 * 1024; // 1MB chunk size

    private byte[]? _cachedKey;

    public async Task Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var buffer = new byte[CHUNK_SIZE];
        int bytesRead;

        while ((bytesRead = await plain.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var nonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);
            var tag = new byte[TAG_SIZE];
            var cipherBytes = new byte[bytesRead];

            using (var aes = new AesGcm(key, TAG_SIZE))
            {
                aes.Encrypt(nonce, buffer.AsSpan(0, bytesRead), cipherBytes, tag);
            }

            var lengthBytes = BitConverter.GetBytes(bytesRead);
            await cipher.WriteAsync(lengthBytes, cancellationToken);
            await cipher.WriteAsync(nonce, cancellationToken);
            await cipher.WriteAsync(tag, cancellationToken);
            await cipher.WriteAsync(cipherBytes, cancellationToken);
        }
    }

    public async Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var lengthBuffer = new byte[4];
        var nonce = new byte[NONCE_SIZE];
        var tag = new byte[TAG_SIZE];

        while (true)
        {
            // Read chunk length
            var read = await ReadExact(cipher, lengthBuffer, cancellationToken);
            if (read == 0)
            {
                break; // End of stream
            }

            var chunkLength = BitConverter.ToInt32(lengthBuffer);
            if (chunkLength is < 0 or > CHUNK_SIZE)
            {
                throw new CryptographicException("Invalid chunk size in cipher stream.");
            }

            // Read metadata
            await ReadExactOrThrow(cipher, nonce, cancellationToken);
            await ReadExactOrThrow(cipher, tag, cancellationToken);

            // Read ciphertext
            var cipherBytes = new byte[chunkLength];
            await ReadExactOrThrow(cipher, cipherBytes, cancellationToken);

            var plainBytes = new byte[chunkLength];
            using (var aes = new AesGcm(key, TAG_SIZE))
            {
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            await plain.WriteAsync(plainBytes, cancellationToken);
        }
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
        if (key.Length != KEY_SIZE && key.Length != 16 && key.Length != 24)
        {
            throw new InvalidOperationException($"Encryption key must be 16, 24, or 32 bytes. Got {key.Length} bytes.");
        }

        _cachedKey = key;
        return key;
    }

    private static async Task<int> ReadExact(Stream stream, byte[] buffer, CancellationToken token)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), token);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }
        return totalRead;
    }

    private static async Task ReadExactOrThrow(Stream stream, byte[] buffer, CancellationToken token)
    {
        var read = await ReadExact(stream, buffer, token);
        if (read != buffer.Length)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading encrypted chunk.");
        }
    }
}
