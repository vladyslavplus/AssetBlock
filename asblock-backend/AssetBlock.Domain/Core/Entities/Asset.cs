using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class Asset : BaseEntity
{
    public required Guid AuthorId { get; set; }
    public required Guid CategoryId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }

    /// <summary>Object key in MinIO (bucket + path).</summary>
    public required string StorageKey { get; set; }
    /// <summary>Original file name (e.g. package.zip).</summary>
    public required string FileName { get; set; }
    /// <summary>AES-GCM nonce (12 bytes) for this file; stored base64.</summary>
    public required string EncryptionNonceBase64 { get; set; }

    public User Author { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
}
