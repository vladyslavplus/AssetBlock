namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Lightweight projection for asset ownership and lifecycle checks without loading full entity graph.
/// </summary>
public sealed record AssetOwnershipDto(
    Guid Id,
    Guid AuthorId,
    bool IsDeleted);
