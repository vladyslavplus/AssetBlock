namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// AES-GCM: Encrypt writes 12-byte nonce at the start of the cipher stream, then ciphertext. Returns that nonce for storage.
/// Decrypt reads 12-byte nonce from the cipher stream, then decrypts the rest to plain.
/// </summary>
public interface IEncryptionService
{
    /// <summary>Encrypts plain to cipher; writes nonce (12 bytes) at the start of each chunk in cipher stream.</summary>
    Task Encrypt(Stream plain, Stream cipher, CancellationToken cancellationToken = default);
    Task Decrypt(Stream cipher, Stream plain, CancellationToken cancellationToken = default);
}
