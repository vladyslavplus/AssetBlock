using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ISocialPlatformStore
{
    Task<List<SocialPlatform>> GetAll(CancellationToken cancellationToken = default);
}
