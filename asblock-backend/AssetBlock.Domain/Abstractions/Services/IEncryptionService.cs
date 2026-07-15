namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// AES-GCM chunked encryption. Wire format per chunk:
/// [4-byte plaintext length][12-byte nonce][16-byte tag][N ciphertext]; trailing 4-byte EOS (0xFFFFFFFF).
/// </summary>
public interface IEncryptionService
{
    /// <summary>Encrypts plain to cipher; writes length/nonce/tag/ciphertext per chunk plus EOS marker.</summary>
    Task Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default);
    Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deterministic ciphertext size for a given plaintext length.
    /// Formula: for each data chunk of size S: 4 + 12 + 16 + S; plus a final 4-byte EOS marker.
    /// Chunks are 1 MiB (1048576) except a possible trailing partial chunk. Empty plaintext yields 4 (EOS only).
    /// </summary>
    long ComputeCiphertextLength(long plaintextLength);
}
