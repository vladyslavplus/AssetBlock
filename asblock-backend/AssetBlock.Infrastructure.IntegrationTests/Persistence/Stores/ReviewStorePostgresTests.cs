using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class ReviewStorePostgresTests(PostgresFixture fixture)
{
    private static ReviewStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<ReviewStore>.Instance);

    [Fact]
    public async Task GetAverageRatingForAsset_WhenNoReviews_ShouldReturnZero()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var sut = CreateStore(db);

        (await sut.GetAverageRatingForAsset(asset.Id)).Should().Be(0d);
    }

    [Fact]
    public async Task GetAverageRatingForAsset_WhenReviewsExist_ShouldReturnMean()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var reviewerA = TestData.CreateUser("reviewer-a", "reviewer-a@example.test");
        var reviewerB = TestData.CreateUser("reviewer-b", "reviewer-b@example.test");
        var reviewerC = TestData.CreateUser("reviewer-c", "reviewer-c@example.test");
        db.Users.AddRange(reviewerA, reviewerB, reviewerC);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        db.Reviews.AddRange(
            TestData.CreateReview(reviewerA.Id, asset.Id, rating: 5),
            TestData.CreateReview(reviewerB.Id, asset.Id, rating: 3),
            TestData.CreateReview(reviewerC.Id, asset.Id, rating: 4));
        await db.SaveChangesAsync();

        var sut = CreateStore(db);

        (await sut.GetAverageRatingForAsset(asset.Id)).Should().Be(4d);
    }
}
