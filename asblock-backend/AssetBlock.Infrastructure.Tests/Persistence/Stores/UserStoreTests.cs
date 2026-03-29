using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class UserStoreTests
{
    [Fact]
    public async Task Create_GetByEmail_GetByIdForUpdate_Update_Delete()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var sut = new UserStore(db);

        var user = await sut.Create("  User1  ", "  EMAIL@test.com  ", "hash");
        user.Email.Should().Be("email@test.com");

        (await sut.GetByEmail("EMAIL@test.com"))!.Id.Should().Be(user.Id);

        var forUpdate = await sut.GetByIdForUpdate(user.Id);
        forUpdate!.Bio = "bio";
        await sut.Update(forUpdate);

        (await sut.GetByIdForUpdate(user.Id))!.Bio.Should().Be("bio");

        await sut.Delete(user.Id);
        (await sut.GetByEmail("email@test.com")).Should().BeNull();
    }

    [Fact]
    public async Task ReplaceUserSocialLinks_and_GetByIdWithLinks()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "soc",
            Email = "s@s.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var platformId = await db.Set<SocialPlatform>().Select(p => p.Id).FirstAsync();

        var sut = new UserStore(db);
        (await sut.ReplaceUserSocialLinks(user.Id, [(platformId, "  https://x.com/a  ")])).Should().BeTrue();

        var withLinks = await sut.GetByIdWithLinks(user.Id);
        withLinks!.SocialLinks.Should().HaveCount(1);
        withLinks.SocialLinks.First().Url.Should().Be("https://x.com/a");

        (await sut.ReplaceUserSocialLinks(Guid.NewGuid(), [])).Should().BeFalse();
    }
}
