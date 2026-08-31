using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        ReviewStore sut = CreateStore(db);

        (await sut.GetAverageRatingForAsset(asset.Id)).Should().Be(0d);
    }

    [Fact]
    public async Task Create_And_Delete_ShouldUpdateAssetRatingAggregatesAndAverageRatingCorrectly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User reviewerA = TestData.CreateUser("reviewer-a", "reviewer-a@example.test");
        User reviewerB = TestData.CreateUser("reviewer-b", "reviewer-b@example.test");
        User reviewerC = TestData.CreateUser("reviewer-c", "reviewer-c@example.test");
        db.Users.AddRange(reviewerA, reviewerB, reviewerC);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        ReviewStore sut = CreateStore(db);

        await sut.Create(TestData.CreateReview(reviewerA.Id, asset.Id, rating: 5));
        Review r2 = await sut.Create(TestData.CreateReview(reviewerB.Id, asset.Id, rating: 3));
        await sut.Create(TestData.CreateReview(reviewerC.Id, asset.Id, rating: 4));

        (await sut.GetAverageRatingForAsset(asset.Id)).Should().Be(4d);

        Asset updatedAsset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        updatedAsset.RatingAverage.Should().Be(4d);
        updatedAsset.RatingCount.Should().Be(3);

        (await sut.Delete(r2.Id)).Should().BeTrue();

        (await sut.GetAverageRatingForAsset(asset.Id)).Should().Be(4.5d);

        Asset assetAfterDelete = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        assetAfterDelete.RatingAverage.Should().Be(4.5d);
        assetAfterDelete.RatingCount.Should().Be(2);

        PagedResult<ReviewListItem> paged = await sut.GetPaged(asset.Id, new Domain.Core.Dto.Reviews.GetReviewsRequest { Page = 1, PageSize = 10, SortBy = "Rating" });
        paged.TotalCount.Should().Be(2);
        paged.Items.Should().HaveCount(2);
        paged.Items.Should().Contain(i => i.Username == "reviewer-a");
        paged.Items.Should().Contain(i => i.Username == "reviewer-c");
    }

    [Fact]
    public async Task Create_WhenCalledConcurrently_ShouldMaintainCorrectRatingAggregate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var reviewers = Enumerable.Range(1, 5)
            .Select(i => TestData.CreateUser($"conc-rev-{i}", $"conc-rev-{i}@example.test"))
            .ToList();
        db.Users.AddRange(reviewers);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var ratings = new[] { 5, 4, 3, 2, 1 }; // avg: 3.0, count: 5

        IEnumerable<Task> tasks = reviewers.Select(async (reviewer, index) =>
        {
            await using ApplicationDbContext taskDb = fixture.CreateDbContext();
            ReviewStore store = CreateStore(taskDb);
            await store.Create(TestData.CreateReview(reviewer.Id, asset.Id, rating: ratings[index]));
        });

        await Task.WhenAll(tasks);

        Asset updatedAsset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        updatedAsset.RatingCount.Should().Be(5);
        updatedAsset.RatingAverage.Should().Be(3.0d);
    }

    [Fact]
    public async Task MixedCreateAndDelete_WhenCalledConcurrently_ShouldMaintainCorrectRatingAggregate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var initialReviewers = Enumerable.Range(1, 2)
            .Select(i => TestData.CreateUser($"init-rev-{i}", $"init-rev-{i}@example.test"))
            .ToList();
        var newReviewers = Enumerable.Range(3, 3)
            .Select(i => TestData.CreateUser($"new-rev-{i}", $"new-rev-{i}@example.test"))
            .ToList();
        db.Users.AddRange(initialReviewers);
        db.Users.AddRange(newReviewers);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        ReviewStore initialStore = CreateStore(db);
        Review r1 = await initialStore.Create(TestData.CreateReview(initialReviewers[0].Id, asset.Id, rating: 5));
        Review r2 = await initialStore.Create(TestData.CreateReview(initialReviewers[1].Id, asset.Id, rating: 2));

        IEnumerable<Task> deleteTasks = new[] { r1.Id, r2.Id }.Select(async reviewId =>
        {
            await using ApplicationDbContext taskDb = fixture.CreateDbContext();
            ReviewStore store = CreateStore(taskDb);
            await store.Delete(reviewId);
        });

        IEnumerable<Task> createTasks = newReviewers.Select(async (reviewer, idx) =>
        {
            await using ApplicationDbContext taskDb = fixture.CreateDbContext();
            ReviewStore store = CreateStore(taskDb);
            await store.Create(TestData.CreateReview(reviewer.Id, asset.Id, rating: 4 + idx % 2));
        });

        await Task.WhenAll(deleteTasks.Concat(createTasks));

        Asset finalAsset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        var actualCount = await db.Reviews.AsNoTracking().CountAsync(r => r.AssetId == asset.Id);
        var actualAvg = actualCount > 0
            ? await db.Reviews.AsNoTracking().Where(r => r.AssetId == asset.Id).AverageAsync(r => (double)r.Rating)
            : 0d;

        finalAsset.RatingCount.Should().Be(actualCount);
        finalAsset.RatingAverage.Should().Be(actualAvg);
    }
}
