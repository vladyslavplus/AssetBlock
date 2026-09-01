using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class CacheKeysTests
{
    [Fact]
    public void DownloadCounter_WhenUtcWindowProvided_ShouldBuildNamespacedHourlyKeyAndExpiry()
    {
        var assetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var now = new DateTimeOffset(2026, 9, 1, 12, 15, 30, TimeSpan.Zero);

        CacheKeys.DownloadCounter(assetId, userId, now).Should().Be(
            "assetblock:downloads:hourly:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:2026090112");
        CacheKeys.DownloadCounterExpiry(now).Should().Be(TimeSpan.FromMinutes(44.5));
    }

    [Fact]
    public void InvalidationPrefixes_WhenReviewListKeyProvided_ShouldReturnAssetSpecificPrefix()
    {
        var assetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var key = $"{CacheKeys.ReviewsListAssetPrefix(assetId)}:1:20:none:createdAt:desc";

        CacheKeys.InvalidationPrefixes(key).Should().Equal(CacheKeys.ReviewsListAssetPrefix(assetId));
    }
}
