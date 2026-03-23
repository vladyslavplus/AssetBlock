namespace AssetBlock.Domain.Core.Dto.Paging;

/// <summary>
/// Base paging/sorting parameters for list endpoints.
/// </summary>
public abstract record PagedRequest
{
    public const int DEFAULT_PAGE = 1;
    public const int MIN_PAGE_SIZE = 1;
    public const int MAX_PAGE_SIZE = 100;
    private const int DEFAULT_PAGE_SIZE = 10;

    public int Page { get; init; } = DEFAULT_PAGE;
    public int PageSize { get; init; } = DEFAULT_PAGE_SIZE;
    public string? SortBy { get; init; }
    public virtual SortDirection SortDirection { get; init; } = SortDirection.ASC;
}
