using AssetBlock.Domain.Core.Entities;
using Pgvector;

namespace AssetBlock.Infrastructure.Persistence.Entities;

public sealed class AssetEmbedding
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public string ModelKey { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string ModelId { get; set; } = null!;
    public string ModelRevision { get; set; } = null!;
    public string ModelDigest { get; set; } = null!;
    public int Dimension { get; set; }
    public string ContentSchemaVersion { get; set; } = null!;
    public long SourceRevision { get; set; }
    public string ContentHash { get; set; } = null!;
    public Vector Embedding { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Asset Asset { get; set; } = null!;
}
