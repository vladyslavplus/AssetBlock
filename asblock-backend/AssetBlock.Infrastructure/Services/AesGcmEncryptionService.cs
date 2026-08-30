using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AesGcmEncryptionService : IEncryptionService, IDisposable
{
    private const int KEY_SIZE = 32;
    private const int NONCE_SIZE = 12;
    private const int TAG_SIZE = 16;
    private const int CHUNK_SIZE = 1024 * 1024; // 1 MB
    private const int CHUNK_LENGTH_FIELD = 4;   // bytes reserved for length prefix
    private const uint END_OF_STREAM_MARKER = uint.MaxValue; // 0xFFFFFFFF sentinel
    private const byte MAX_KEY_ID_BYTES = 64;

    // Magic bytes for v1 header: "ABE1"
    private static readonly byte[] _magic = [.. "ABE1"u8];

    private readonly Dictionary<string, KeyEntry> _keyRing = new(StringComparer.OrdinalIgnoreCase);
    private readonly KeyEntry _activeKeyEntry;
    private readonly KeyEntry _legacyKeyEntry;
    private readonly byte[] _activeKeyIdBytes;
    private bool _disposed;

    public AesGcmEncryptionService(IOptions<EncryptionOptions> options)
    {
        var opt = options.Value;
        if (opt.Keys is { Count: > 0 })
        {
            var activeId = string.IsNullOrWhiteSpace(opt.CurrentKeyId) ? EncryptionOptions.DEFAULT_KEY_ID : opt.CurrentKeyId;
            var legacyId = string.IsNullOrWhiteSpace(opt.LegacyKeyId) ? activeId : opt.LegacyKeyId;

            foreach (var (keyId, keyBase64) in opt.Keys)
            {
                var entry = CreateKeyEntry(keyId, keyBase64);
                _keyRing[keyId] = entry;
            }

            if (!_keyRing.TryGetValue(activeId, out var activeEntry))
            {
                throw new InvalidOperationException($"Encryption:CurrentKeyId '{activeId}' was not found in Encryption:Keys.");
            }

            if (!_keyRing.TryGetValue(legacyId, out var legacyEntry))
            {
                throw new InvalidOperationException($"Encryption:LegacyKeyId '{legacyId}' was not found in Encryption:Keys.");
            }

            _activeKeyEntry = activeEntry;
            _legacyKeyEntry = legacyEntry;
            _activeKeyIdBytes = Encoding.UTF8.GetBytes(activeId);
        }
        else
        {
            var activeId = string.IsNullOrWhiteSpace(opt.CurrentKeyId) ? EncryptionOptions.DEFAULT_KEY_ID : opt.CurrentKeyId;
            if (string.IsNullOrWhiteSpace(opt.KeyBase64))
            {
                throw new InvalidOperationException("Encryption:KeyBase64 is not configured.");
            }

            var entry = CreateKeyEntry(activeId, opt.KeyBase64);
            _keyRing[activeId] = entry;
            _activeKeyEntry = entry;
            _legacyKeyEntry = entry;
            _activeKeyIdBytes = Encoding.UTF8.GetBytes(activeId);
        }

        if (_activeKeyIdBytes.Length > MAX_KEY_ID_BYTES)
        {
            throw new InvalidOperationException($"Active encryption key ID exceeds maximum length of {MAX_KEY_ID_BYTES} bytes.");
        }
    }

    // Wire format v1:
    //   [4 bytes  : Magic "ABE1" (0x41, 0x42, 0x45, 0x31)                  ]
    //   [1 byte   : KeyId length (K)                                       ]
    //   [K bytes  : KeyId (UTF-8)                                          ]
    // Followed by chunk stream:
    //   [4 bytes  : uint plaintext chunk length  (0xFFFFFFFF = end marker) ]
    //   [12 bytes : nonce                                                  ]
    //   [16 bytes : GCM tag                                                ]
    //   [N bytes  : ciphertext                                             ]
    // AAD per chunk = chunk index as little-endian int64 (prevents reorder)
    // Trailing 4-byte end marker (END_OF_STREAM_MARKER) detects truncation.

    public async Task Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Write header
        await cipher.WriteAsync(_magic, cancellationToken);
        await cipher.WriteAsync(new[] { (byte)_activeKeyIdBytes.Length }, cancellationToken);
        await cipher.WriteAsync(_activeKeyIdBytes, cancellationToken);

        using var aesGcm = new AesGcm(_activeKeyEntry.KeyBytes, TAG_SIZE);
        var plainBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        var cipherBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        var nonce = new byte[NONCE_SIZE];
        var tag = new byte[TAG_SIZE];
        var aad = new byte[sizeof(long)];
        long chunkIndex = 0;

        try
        {
            while (true)
            {
                var bytesRead = await plain.ReadAtLeastAsync(
                    plainBuffer.AsMemory(0, CHUNK_SIZE),
                    CHUNK_SIZE,
                    throwOnEndOfStream: false,
                    cancellationToken: cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteInt64LittleEndian(aad, chunkIndex);

                aesGcm.Encrypt(
                    nonce,
                    plainBuffer.AsSpan(0, bytesRead),
                    cipherBuffer.AsSpan(0, bytesRead),
                    tag,
                    aad);

                await cipher.WriteAsync(BitConverter.GetBytes((uint)bytesRead), cancellationToken);
                await cipher.WriteAsync(nonce, cancellationToken);
                await cipher.WriteAsync(tag, cancellationToken);
                await cipher.WriteAsync(cipherBuffer.AsMemory(0, bytesRead), cancellationToken);

                chunkIndex++;
            }

            // Write end-of-stream marker so Decrypt can detect truncation.
            await cipher.WriteAsync(BitConverter.GetBytes(END_OF_STREAM_MARKER), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plainBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipherBuffer, clearArray: true);
        }
    }

    public async Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var headerMagicOrLength = new byte[CHUNK_LENGTH_FIELD];
        var initialRead = await ReadExact(cipher, headerMagicOrLength, cancellationToken);
        if (initialRead == 0)
        {
            throw new CryptographicException("Cipher stream was truncated: missing end-of-stream marker.");
        }

        if (initialRead < CHUNK_LENGTH_FIELD)
        {
            throw new CryptographicException("Cipher stream is corrupt: partial header/length field.");
        }

        if (headerMagicOrLength.AsSpan().SequenceEqual(_magic))
        {
            // V1 format with header
            var keyIdLenByte = new byte[1];
            await ReadExactOrThrow(cipher, keyIdLenByte, cancellationToken);
            var keyIdLen = keyIdLenByte[0];
            if (keyIdLen is 0 or > MAX_KEY_ID_BYTES)
            {
                throw new CryptographicException("Cipher stream is corrupt: invalid key identifier length.");
            }

            var keyIdBytes = new byte[keyIdLen];
            await ReadExactOrThrow(cipher, keyIdBytes, cancellationToken);
            var keyId = Encoding.UTF8.GetString(keyIdBytes);

            if (!_keyRing.TryGetValue(keyId, out var keyEntry))
            {
                throw new CryptographicException($"Unknown encryption key ID '{keyId}'.");
            }

            using var aesGcm = new AesGcm(keyEntry.KeyBytes, TAG_SIZE);
            await DecryptChunks(cipher, plain, aesGcm, initialChunkLength: null, cancellationToken);
        }
        else
        {
            // Legacy headerless format: first 4 bytes are the first chunk length
            var firstChunkLength = BitConverter.ToUInt32(headerMagicOrLength);
            using var aesGcm = new AesGcm(_legacyKeyEntry.KeyBytes, TAG_SIZE);
            await DecryptChunks(cipher, plain, aesGcm, firstChunkLength, cancellationToken);
        }
    }

    private static async Task DecryptChunks(
        Stream cipher,
        Stream plain,
        AesGcm aesGcm,
        uint? initialChunkLength,
        CancellationToken cancellationToken)
    {
        var cipherBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        var plainBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        var lengthBuffer = new byte[CHUNK_LENGTH_FIELD];
        var nonce = new byte[NONCE_SIZE];
        var tag = new byte[TAG_SIZE];
        var aad = new byte[sizeof(long)];
        long chunkIndex = 0;

        try
        {
            var isFirstChunk = true;
            while (true)
            {
                uint chunkLength;
                if (isFirstChunk && initialChunkLength.HasValue)
                {
                    chunkLength = initialChunkLength.Value;
                }
                else
                {
                    chunkLength = await ReadChunkLengthOrThrow(cipher, lengthBuffer, cancellationToken);
                }

                isFirstChunk = false;

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
                await ReadExactOrThrow(cipher, cipherBuffer.AsMemory(0, (int)chunkLength), cancellationToken);

                BinaryPrimitives.WriteInt64LittleEndian(aad, chunkIndex);

                aesGcm.Decrypt(
                    nonce,
                    cipherBuffer.AsSpan(0, (int)chunkLength),
                    tag,
                    plainBuffer.AsSpan(0, (int)chunkLength),
                    aad);

                await plain.WriteAsync(plainBuffer.AsMemory(0, (int)chunkLength), cancellationToken);
                chunkIndex++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(plainBuffer, clearArray: true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Header overhead: 4 (Magic) + 1 (KeyId length) + K (KeyId length).
    /// Per data chunk of plaintext size S: 4 (length) + 12 (nonce) + 16 (tag) + S (ciphertext).
    /// Full chunks use S = 1 MiB; a final partial chunk uses the remainder. Always +4 for EOS.
    /// </remarks>
    public long ComputeCiphertextLength(long plaintextLength)
    {
        if (plaintextLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintextLength));
        }

        var headerLength = _magic.Length + 1 + _activeKeyIdBytes.Length;
        const int overheadPerChunk = CHUNK_LENGTH_FIELD + NONCE_SIZE + TAG_SIZE;
        var fullChunks = plaintextLength / CHUNK_SIZE;
        var remainder = plaintextLength % CHUNK_SIZE;

        long length = headerLength + (fullChunks * (overheadPerChunk + CHUNK_SIZE));
        if (remainder > 0)
        {
            length += overheadPerChunk + remainder;
        }

        length += CHUNK_LENGTH_FIELD; // EOS marker
        return length;
    }

    private static KeyEntry CreateKeyEntry(string keyId, string keyBase64)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Encryption key '{keyId}' is not valid Base64.", ex);
        }

        if (keyBytes.Length != KEY_SIZE)
        {
            throw new InvalidOperationException($"Encryption key must be exactly {KEY_SIZE} bytes for AES-256. Got {keyBytes.Length} bytes.");
        }

        return new KeyEntry(keyId, keyBytes);
    }

    private static async Task<uint> ReadChunkLengthOrThrow(Stream stream, byte[] lengthBuffer, CancellationToken token)
    {
        var read = await ReadExact(stream, lengthBuffer, token);
        if (read == 0)
        {
            throw new CryptographicException("Cipher stream was truncated: missing end-of-stream marker.");
        }

        return read < lengthBuffer.Length
            ? throw new CryptographicException("Cipher stream is corrupt: partial length field.")
            : BitConverter.ToUInt32(lengthBuffer);
    }

    private static async Task<int> ReadExact(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], token);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async Task ReadExactOrThrow(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = await ReadExact(stream, buffer, token);
        if (read != buffer.Length)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading encrypted chunk.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _keyRing.Values)
        {
            entry.Dispose();
        }

        _keyRing.Clear();
    }

    private sealed class KeyEntry(string keyId, byte[] keyBytes) : IDisposable
    {
        public string KeyId { get; } = keyId;
        public byte[] KeyBytes { get; } = keyBytes;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(KeyBytes);
        }
    }
}
