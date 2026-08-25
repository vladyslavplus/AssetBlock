using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Configurations;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class ListingCopilotStorePostgresTests(PostgresFixture fixture)
{
    private static AssetProcessingJobStore CreateJobStore(ApplicationDbContext db) =>
        new(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

    private static ListingCopilotStore CreateCopilotStore(ApplicationDbContext db) => new(db);

    [Fact]
    public async Task TryCommitSucceeded_WhenLeaseValid_ShouldPersistSuggestionAndSucceedJob()
    {
        var seed = await SeedClaimedJob();
        var suggestion = SampleWrite(seed.JobId, seed.Version.Id);

        var committed = await seed.CopilotStore.TryCommitSucceeded(
            seed.JobId,
            seed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            suggestion);

        committed.Should().BeTrue();
        var stored = await seed.CopilotStore.GetSuggestionForOwner(seed.Version.Id, seed.Author.Id);
        stored.Should().NotBeNull();
        stored.Title.Should().Be("Chair");
        stored.ActualModel.Should().Be("fixture/openrouter-test");
        var job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        job.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task TryCommitSucceeded_WhenLeaseExpired_ShouldReturnFalseAndKeepRunning()
    {
        var seed = await SeedClaimedJob();
        await seed.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE asset_processing_jobs SET "LeaseExpiresAt" = clock_timestamp() - INTERVAL '10 seconds' WHERE "Id" = {seed.JobId}""");

        var committed = await seed.CopilotStore.TryCommitSucceeded(
            seed.JobId,
            seed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            SampleWrite(seed.JobId, seed.Version.Id));

        committed.Should().BeFalse();
        (await seed.Db.AssetListingSuggestions.CountAsync()).Should().Be(0);
        var job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task TryCommitSucceeded_WhenLeaseTokenMismatches_ShouldReturnFalseAndKeepRunning()
    {
        var seed = await SeedClaimedJob();

        var committed = await seed.CopilotStore.TryCommitSucceeded(
            seed.JobId,
            Guid.NewGuid(),
            seed.Asset.Id,
            seed.Version.Id,
            SampleWrite(seed.JobId, seed.Version.Id));

        committed.Should().BeFalse();
        (await seed.Db.AssetListingSuggestions.CountAsync()).Should().Be(0);
        var job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        job.LeaseToken.Should().Be(seed.LeaseToken);
    }

    [Fact]
    public async Task GetSuggestionForOwner_WhenForeignUser_ShouldReturnNull()
    {
        var seed = await SeedClaimedJob();
        (await seed.CopilotStore.TryCommitSucceeded(
            seed.JobId,
            seed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            SampleWrite(seed.JobId, seed.Version.Id))).Should().BeTrue();

        var other = await seed.CopilotStore.GetSuggestionForOwner(seed.Version.Id, Guid.NewGuid());
        other.Should().BeNull();
    }

    [Fact]
    public async Task Constraints_WhenContentHashInvalid_ShouldReject()
    {
        var seed = await SeedClaimedJob();
        var write = SampleWrite(seed.JobId, seed.Version.Id) with { ContentHash = "not-a-hash" };

        var act = async () => await seed.CopilotStore.TryCommitSucceeded(
            seed.JobId,
            seed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            write);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Constraints_WhenElevenTags_ShouldReject()
    {
        var seed = await SeedClaimedJob();
        var tags = string.Join(",", Enumerable.Range(0, 11).Select(i => $"\"t{i}\""));

        var act = async () => await InsertSuggestionRaw(seed, $"[{tags}]");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
        ex.Which.ConstraintName.Should().Be(AssetListingSuggestionConfiguration.CK_TAGS_LENGTH);
    }

    [Fact]
    public async Task Constraints_WhenTagElementIsNotString_ShouldReject()
    {
        var seed = await SeedClaimedJob();

        var act = async () => await InsertSuggestionRaw(seed, """["ok", 123]""");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
        ex.Which.ConstraintName.Should().Be(AssetListingSuggestionConfiguration.CK_TAGS_ITEMS);
    }

    private static async Task InsertSuggestionRaw(Seed seed, string tagsJson)
    {
        await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asset_listing_suggestions (
                "JobId",
                "PromptPolicyVersion",
                "Provider",
                "ModelId",
                "Title",
                "Description",
                "Category",
                "Tags",
                "ContentHash",
                "CreatedAt")
            VALUES (
                {seed.JobId},
                {AiPromptPolicies.LISTING_COPILOT_V1},
                'OPENROUTER',
                'fixture/openrouter-test',
                'Chair',
                'A chair',
                '3D',
                {tagsJson}::jsonb,
                {new string('a', 64)},
                clock_timestamp())
            """);
    }

    private async Task<Seed> SeedClaimedJob()
    {
        var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id, fileName: "pack.zip");
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        db.AssetArchiveAnalyses.Add(new AssetArchiveAnalysis
        {
            AssetVersionId = version.Id,
            FileCount = 1,
            TotalExpandedBytes = 10,
            ReadmeContent = "readme",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            asset.Id,
            version.Id,
            AssetProcessingJobType.LISTING_COPILOT,
            AiPromptPolicies.LISTING_COPILOT_DEFINITION_VERSION,
            TimeSpan.Zero,
            new ListingCopilotPayload(AiPromptPolicies.LISTING_COPILOT_V1));
        var claimed = await jobStore.ClaimPendingBatch(1, TimeSpan.FromMinutes(1), "test-worker");
        claimed.Should().HaveCount(1);

        return new Seed(db, author, asset, version, CreateCopilotStore(db), claimed[0].JobId, claimed[0].LeaseToken);
    }

    private static ListingCopilotSuggestionWrite SampleWrite(Guid jobId, Guid _)
    {
        var suggestion = new ListingSuggestion("Chair", "A chair", "3D", ["lowpoly"]);
        return new ListingCopilotSuggestionWrite(
            jobId,
            AiPromptPolicies.LISTING_COPILOT_V1,
            AiProviderKind.OPENROUTER,
            "fixture/openrouter-test",
            null,
            "TestHost",
            "gen-secret",
            suggestion.Title,
            suggestion.Description,
            suggestion.Category,
            suggestion.Tags,
            ListingSuggestionCanonicalizer.ComputeContentHash(suggestion),
            1,
            2);
    }

    private sealed record Seed(
        ApplicationDbContext Db,
        User Author,
        Asset Asset,
        AssetVersion Version,
        ListingCopilotStore CopilotStore,
        Guid JobId,
        Guid LeaseToken);
}
