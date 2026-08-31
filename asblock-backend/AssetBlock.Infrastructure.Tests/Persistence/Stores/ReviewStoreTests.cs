using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class ReviewStoreTests
{
    [Fact]
    public async Task GetById_GetPaged_Exists_GetAverageRating()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
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
        var reviewer = new User
        {
            Id = reviewerId,
            Username = "rev",
            Email = "r@r.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(reviewer);
        var assetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = catId,
            Title = "A",
            RatingAverage = 5d,
            RatingCount = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var reviewId = Guid.NewGuid();
        var review = new Review
        {
            Id = reviewId,
            AssetId = assetId,
            UserId = reviewerId,
            Rating = 5,
            Comment = "nice",
            CreatedAt = DateTimeOffset.UtcNow,
            User = reviewer
        };
        db.Reviews.Add(review);
        await db.SaveChangesAsync();

        var sut = new ReviewStore(db, NullLogger<ReviewStore>.Instance);

        (await sut.Exists(reviewerId, assetId)).Should().BeTrue();

        Review? byId = await sut.GetById(review.Id);
        byId.Should().NotBeNull();
        byId.User.Should().NotBeNull();
        byId.User.Username.Should().Be("rev");

        PagedResult<ReviewListItem> paged = await sut.GetPaged(assetId, new GetReviewsRequest { Page = 1, PageSize = 10, SortBy = "Rating" });
        paged.Items.Should().Contain(r => r.Id == review.Id);
        paged.Items[0].Username.Should().Be("rev");

        var avg = await sut.GetAverageRatingForAsset(assetId);
        avg.Should().Be(5d);
    }
}
