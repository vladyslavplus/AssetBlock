using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class SocialPlatformStore(ApplicationDbContext dbContext, ICacheService cache) : ISocialPlatformStore
{
    public async Task<List<SocialPlatform>> GetAll(CancellationToken cancellationToken = default)
    {
        var cachedStr = await cache.GetString(CacheKeys.SOCIAL_PLATFORMS, cancellationToken);
        if (cachedStr is not null)
        {
            List<SocialPlatform>? cached = System.Text.Json.JsonSerializer.Deserialize<List<SocialPlatform>>(cachedStr);
            if (cached is not null)
            {
                return cached;
            }
        }

        List<SocialPlatform> platforms = await dbContext.Set<SocialPlatform>()
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        await cache.SetString(CacheKeys.SOCIAL_PLATFORMS, System.Text.Json.JsonSerializer.Serialize(platforms), TimeSpan.FromHours(24), cancellationToken);
        return platforms;
    }
}
