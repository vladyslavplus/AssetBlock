namespace AssetBlock.Domain.Dto.Paging;

/// <summary>
/// Base paging/sorting parameters for list endpoints.
/// </summary>
public abstract class PagedRequest
{
    public const int DEFAULT_PAGE = 1;
    public const int MIN_PAGE_SIZE = 1;
    public const int MAX_PAGE_SIZE = 100;
    private const int DEFAULT_PAGE_SIZE = 10;

    public int Page { get; set; } = DEFAULT_PAGE;
    public int PageSize { get; set; } = DEFAULT_PAGE_SIZE;
    public string? SortBy { get; set; }
    public SortDirection SortDirection { get; set; } = SortDirection.ASC;
}
