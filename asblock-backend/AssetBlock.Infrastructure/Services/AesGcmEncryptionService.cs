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
    private const int CHUNK_SIZE = 1024 * 1024; // 1 MB
    private const int CHUNK_LENGTH_FIELD = 4;   // bytes reserved for length prefix
    private const uint END_OF_STREAM_MARKER = uint.MaxValue; // 0xFFFFFFFF sentinel

    private byte[]? _cachedKey;

    // Wire format per chunk:
    //   [4 bytes  : uint plaintext chunk length  (0xFFFFFFFF = end marker)]
    //   [12 bytes : nonce                                                  ]
    //   [16 bytes : GCM tag                                                ]
    //   [N bytes  : ciphertext                                             ]
    // AAD per chunk = chunk index as little-endian int64 (prevents reorder)
    // Trailing 4-byte end marker (END_OF_STREAM_MARKER) detects truncation.

    public async Task Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var buffer = new byte[CHUNK_SIZE];
        int bytesRead;
        long chunkIndex = 0;

        while ((bytesRead = await plain.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var nonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);
            var tag = new byte[TAG_SIZE];
            var cipherBytes = new byte[bytesRead];
            var aad = BitConverter.GetBytes(chunkIndex); // int64 AAD = chunk position

            using (var aes = new AesGcm(key, TAG_SIZE))
            {
                aes.Encrypt(nonce, buffer.AsSpan(0, bytesRead), cipherBytes, tag, aad);
            }

            await cipher.WriteAsync(BitConverter.GetBytes((uint)bytesRead), cancellationToken);
            await cipher.WriteAsync(nonce, cancellationToken);
            await cipher.WriteAsync(tag, cancellationToken);
            await cipher.WriteAsync(cipherBytes, cancellationToken);

            chunkIndex++;
        }

        // Write end-of-stream marker so Decrypt can detect truncation.
        await cipher.WriteAsync(BitConverter.GetBytes(END_OF_STREAM_MARKER), cancellationToken);
    }

    public async Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default)
    {
        var key = GetKey();
        var lengthBuffer = new byte[CHUNK_LENGTH_FIELD];
        var nonce = new byte[NONCE_SIZE];
        var tag = new byte[TAG_SIZE];
        long chunkIndex = 0;

        while (true)
        {
            // Read chunk length / end marker
            var read = await ReadExact(cipher, lengthBuffer, cancellationToken);
            if (read == 0)
            {
                // Reached EOF without seeing the end-of-stream marker.
                throw new CryptographicException("Cipher stream was truncated: missing end-of-stream marker.");
            }

            if (read < CHUNK_LENGTH_FIELD)
            {
                throw new CryptographicException("Cipher stream is corrupt: partial length field.");
            }

            var chunkLength = BitConverter.ToUInt32(lengthBuffer);
            if (chunkLength == END_OF_STREAM_MARKER)
            {
                break; // Proper end of stream.
            }

            if (chunkLength > CHUNK_SIZE)
            {
                throw new CryptographicException($"Invalid chunk size {chunkLength}: exceeds maximum {CHUNK_SIZE}.");
            }

            await ReadExactOrThrow(cipher, nonce, cancellationToken);
            await ReadExactOrThrow(cipher, tag, cancellationToken);

            var cipherBytes = new byte[chunkLength];
            await ReadExactOrThrow(cipher, cipherBytes, cancellationToken);

            var plainBytes = new byte[chunkLength];
            var aad = BitConverter.GetBytes(chunkIndex); // must match index used during Encrypt

            using (var aes = new AesGcm(key, TAG_SIZE))
            {
                // Throws AuthenticationTagMismatchException if chunk is tampered or reordered.
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes, aad);
            }

            await plain.WriteAsync(plainBytes, cancellationToken);
            chunkIndex++;
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
        if (key.Length != 16 && key.Length != 24 && key.Length != KEY_SIZE)
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
