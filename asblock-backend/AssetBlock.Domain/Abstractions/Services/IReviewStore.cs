using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IReviewStore
{
    Task<Review> Create(Guid assetId, Guid userId, int rating, string? comment, CancellationToken cancellationToken = default);
    Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
    Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<Review?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Review>> GetPaged(Guid assetId, GetReviewsRequest request, CancellationToken cancellationToken = default);
}
