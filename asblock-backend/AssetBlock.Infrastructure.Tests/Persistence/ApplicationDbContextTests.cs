using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence;

public sealed class ApplicationDbContextTests
{
    [Fact]
    public async Task Model_exposes_configured_entities()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;

        db.Model.FindEntityType(typeof(User)).Should().NotBeNull();
        db.Model.FindEntityType(typeof(Asset)).Should().NotBeNull();
        db.Model.FindEntityType(typeof(SocialPlatform)).Should().NotBeNull();
    }
}
