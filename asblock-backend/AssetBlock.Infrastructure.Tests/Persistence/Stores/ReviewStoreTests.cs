using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class ReviewStoreTests
{
    [Fact]
    public async Task Create_GetById_GetPaged_Exists_Delete()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var authorId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = authorId,
            Username = "auth",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Users.Add(new User
        {
            Id = reviewerId,
            Username = "rev",
            Email = "r@r.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var assetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = catId,
            Title = "A",
            StorageKey = "k",
            FileName = "f",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new ReviewStore(db, NullLogger<ReviewStore>.Instance);

        var review = await sut.Create(assetId, reviewerId, 5, "nice");
        (await sut.Exists(reviewerId, assetId)).Should().BeTrue();

        var byId = await sut.GetById(review.Id);
        byId!.User.Should().NotBeNull();

        var paged = await sut.GetPaged(assetId, new GetReviewsRequest { Page = 1, PageSize = 10, SortBy = "Rating" });
        paged.Items.Should().Contain(r => r.Id == review.Id);

        (await sut.Delete(review.Id)).Should().BeTrue();
        (await sut.Delete(review.Id)).Should().BeFalse();
    }
}
