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

    /// <summary>Optional max downloads per hour per user. Null = unlimited.</summary>
    public int? DownloadLimitPerHour { get; set; }

    public User Author { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
    public ICollection<Review> Reviews { get; set; } = new List<Review>();
}
