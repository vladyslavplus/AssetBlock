namespace AssetBlock.Domain.Core.Dto.Assets;

public record AssetDocument
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Price { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = "";
    public string CategorySlug { get; init; } = "";
    public Guid AuthorId { get; init; }
    public string AuthorUsername { get; init; } = "";
    public string StorageKey { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public double AverageRating { get; init; }
}
