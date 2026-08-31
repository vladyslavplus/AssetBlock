using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// PostgreSQL integration tests for EmailActionStore.
/// NOTE: Tests that touch email_actions will fail until the migration adding that table is applied.
/// This is expected; the tests are written correctly for post-migration use.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class EmailActionStorePostgresTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static EmailActionStore CreateStore(ApplicationDbContext db) => new(db);

    [Fact]
    public async Task IssueOrReplace_WhenCalledTwiceForSamePurpose_ShouldNotCreateSecondRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionuser1", "actionuser1@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        EmailActionStore store = CreateStore(db);
        EmailAction first = await store.IssueOrReplace(user.Id, EmailActionPurpose.EMAIL_VERIFICATION, user.Email, TimeSpan.FromHours(24));
        EmailAction second = await store.IssueOrReplace(user.Id, EmailActionPurpose.EMAIL_VERIFICATION, user.Email, TimeSpan.FromHours(24));

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        List<EmailAction> rows = await verify.EmailActions
            .Where(a => a.UserId == user.Id && a.Purpose == EmailActionPurpose.EMAIL_VERIFICATION)
            .ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].Id.Should().Be(first.Id);
        second.Id.Should().Be(first.Id);
        second.Version.Should().NotBe(first.Version, because: "IssueOrReplace rotates Version");
    }

    [Fact]
    public async Task TryConsume_WhenCalledOnce_ShouldSucceedAndSetConsumedAt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionuser2", "actionuser2@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        EmailActionStore store = CreateStore(db);
        EmailAction action = await store.IssueOrReplace(user.Id, EmailActionPurpose.PASSWORD_RESET, user.Email, TimeSpan.FromMinutes(30));

        var firstResult = await store.TryConsume(action.Id, EmailActionPurpose.PASSWORD_RESET, action.Version, user.Email);

        firstResult.Should().BeTrue();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        EmailAction row = await verify.EmailActions.AsNoTracking().SingleAsync(a => a.Id == action.Id);
        row.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryConsume_WhenCalledTwice_ShouldSucceedOnlyOnce()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionuser3", "actionuser3@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        EmailActionStore store = CreateStore(db);
        EmailAction action = await store.IssueOrReplace(user.Id, EmailActionPurpose.EMAIL_VERIFICATION, user.Email, TimeSpan.FromHours(24));

        var firstResult = await store.TryConsume(action.Id, EmailActionPurpose.EMAIL_VERIFICATION, action.Version, user.Email);
        var secondResult = await store.TryConsume(action.Id, EmailActionPurpose.EMAIL_VERIFICATION, action.Version, user.Email);

        firstResult.Should().BeTrue();
        secondResult.Should().BeFalse(because: "action is already consumed");
    }

    [Fact]
    public Task EmailActionDispatch_Payload_ShouldNotContainTokenOrUrlOrPasswordOrBody()
    {
        try
        {
            var payload = new EmailActionDispatchPayload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                EmailTemplateKind.EMAIL_VERIFICATION);

            var json = JsonSerializer.Serialize(payload, _json);
            var jsonLower = json.ToLowerInvariant();

            jsonLower.Should().NotContain("\"token\"");
            jsonLower.Should().NotContain("url");
            jsonLower.Should().NotContain("password");
            jsonLower.Should().NotContain("body");
            json.Should().Contain("emailActionId");
            json.Should().Contain("actionVersion");
            json.Should().Contain("recipientUserId");
            json.Should().Contain("templateKind");
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    [Fact]
    public async Task EmailVerifiedAt_ColumnExists_ShouldBeNullableOnNewUser()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionuser4", "actionuser4@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        User reloaded = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        reloaded.EmailVerifiedAt.Should().BeNull(because: "new users have unverified email");
    }

    [Fact]
    public async Task IssueOrReplace_ShouldSetLastSentAt_AllowingCooldownDetection()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionuser5", "actionuser5@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        EmailActionStore store = CreateStore(db);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        EmailAction action = await store.IssueOrReplace(user.Id, EmailActionPurpose.EMAIL_VERIFICATION, user.Email, TimeSpan.FromHours(24));

        action.LastSentAt.Should().NotBeNull();
        action.LastSentAt!.Value.Should().BeCloseTo(before, precision: TimeSpan.FromSeconds(5));

        var isInCooldown = store.IsInCooldown(action, EmailActionConstants.ResendCooldown, DateTimeOffset.UtcNow);
        isInCooldown.Should().BeTrue(because: "just issued — within 60s cooldown window");
    }

    [Fact]
    public async Task IssueOrReplace_WhenTwoContextsRaceFirstInsert_ShouldLeaveSingleRow()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser("actionrace", "actionrace@example.test");
        seedDb.Users.Add(user);
        await seedDb.SaveChangesAsync();
        Guid userId = user.Id;
        var email = user.Email;

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        EmailActionStore storeA = CreateStore(dbA);
        EmailActionStore storeB = CreateStore(dbB);

        Task<EmailAction> taskA = storeA.IssueOrReplace(userId, EmailActionPurpose.EMAIL_VERIFICATION, email, TimeSpan.FromHours(24));
        Task<EmailAction> taskB = storeB.IssueOrReplace(userId, EmailActionPurpose.EMAIL_VERIFICATION, email, TimeSpan.FromHours(24));
        await Task.WhenAll(taskA, taskB);

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        List<EmailAction> rows = await verify.EmailActions
            .Where(a => a.UserId == userId && a.Purpose == EmailActionPurpose.EMAIL_VERIFICATION)
            .ToListAsync();

        rows.Should().HaveCount(1);
        Guid[] versions = new[] { (await taskA).Version, (await taskB).Version };
        versions.Should().Contain(rows[0].Version);
    }
}
