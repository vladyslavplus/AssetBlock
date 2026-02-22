using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ITagStore
{
    Task<PagedResult<Tag>> SearchTags(GetTagsRequest request, CancellationToken cancellationToken = default);
    Task<List<Tag>> GetTagsByNames(List<string> names, CancellationToken cancellationToken = default);
    Task<Tag?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<Tag?> GetByName(string name, CancellationToken cancellationToken = default);
    Task<Tag> Add(Tag tag, CancellationToken cancellationToken = default);
    Task<Tag> Update(Tag tag, CancellationToken cancellationToken = default);
    Task Delete(Tag tag, CancellationToken cancellationToken = default);
}
