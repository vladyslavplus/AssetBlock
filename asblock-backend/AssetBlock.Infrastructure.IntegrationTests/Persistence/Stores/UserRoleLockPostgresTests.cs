using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class UserRoleLockPostgresTests(PostgresFixture fixture)
{
    private static readonly TimeSpan _syncTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LockAndReadPersistedRole_WhenUserTrackedInSameContext_ShouldReturnFreshRoleAfterConcurrentRevocation()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        User moderator = TestData.CreateUser("mod2", "mod2@example.test");
        moderator.Role = AppRoles.MODERATOR;
        seedDb.Users.Add(moderator);
        await seedDb.SaveChangesAsync();

        using ManualResetEventSlim revokeHoldsLock = new(false);
        using ManualResetEventSlim readerLockAttemptStarted = new(false);

        Task<UserPersistedRole?> readTask = Task.Run(async () =>
        {
            revokeHoldsLock.Wait(_syncTimeout).Should().BeTrue();
            await using ApplicationDbContext readDb = fixture.CreateDbContext();
            await using IDbContextTransaction readTx = await readDb.Database.BeginTransactionAsync();
            _ = await readDb.Users.FirstAsync(u => u.Id == moderator.Id);
            readerLockAttemptStarted.Set();
            var readStore = new UserStore(readDb);
            UserPersistedRole? fresh = await readStore.LockAndReadPersistedRole(moderator.Id);
            await readTx.CommitAsync();
            return fresh;
        });

        var revokeTask = Task.Run(async () =>
        {
            await using ApplicationDbContext revokeDb = fixture.CreateDbContext();
            await using IDbContextTransaction revokeTx = await revokeDb.Database.BeginTransactionAsync();
            var revokeStore = new UserStore(revokeDb);
            UserPersistedRole? locked = await revokeStore.LockAndReadPersistedRole(moderator.Id);
            locked!.Role.Should().Be(AppRoles.MODERATOR);
            revokeHoldsLock.Set();
            readerLockAttemptStarted.Wait(_syncTimeout).Should().BeTrue();
            await Task.Delay(100);
            await revokeStore.TryAssignRole(moderator.Id, AppRoles.USER, locked.RoleRevision);
            await revokeTx.CommitAsync();
        });

        await Task.WhenAll(revokeTask, readTask);
        (await readTask)!.Role.Should().Be(AppRoles.USER);
    }

    [Fact]
    public async Task LockAndReadPersistedRole_WhenReaderHoldsLockFirst_RevocationWaitsThenCompletesWithoutDeadlock()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        User moderator = TestData.CreateUser("mod3", "mod3@example.test");
        moderator.Role = AppRoles.MODERATOR;
        seedDb.Users.Add(moderator);
        await seedDb.SaveChangesAsync();

        using ManualResetEventSlim readerHoldsLock = new(false);
        using ManualResetEventSlim revokeMayProceed = new(false);

        var readTask = Task.Run(async () =>
        {
            await using ApplicationDbContext readDb = fixture.CreateDbContext();
            await using IDbContextTransaction readTx = await readDb.Database.BeginTransactionAsync();
            _ = await readDb.Users.FirstAsync(u => u.Id == moderator.Id);
            var readStore = new UserStore(readDb);
            UserPersistedRole? locked = await readStore.LockAndReadPersistedRole(moderator.Id);
            locked!.Role.Should().Be(AppRoles.MODERATOR);
            readerHoldsLock.Set();
            revokeMayProceed.Wait(_syncTimeout).Should().BeTrue();
            await readTx.CommitAsync();
        });

        var revokeTask = Task.Run(async () =>
        {
            readerHoldsLock.Wait(_syncTimeout).Should().BeTrue();
            await using ApplicationDbContext revokeDb = fixture.CreateDbContext();
            await using IDbContextTransaction revokeTx = await revokeDb.Database.BeginTransactionAsync();
            revokeMayProceed.Set();
            var revokeStore = new UserStore(revokeDb);
            UserPersistedRole? locked = await revokeStore.LockAndReadPersistedRole(moderator.Id);
            locked!.Role.Should().Be(AppRoles.MODERATOR);
            await revokeStore.TryAssignRole(moderator.Id, AppRoles.USER, locked.RoleRevision);
            await revokeTx.CommitAsync();
        });

        await Task.WhenAll(readTask, revokeTask);

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        User reloaded = await verifyDb.Users.AsNoTracking().FirstAsync(u => u.Id == moderator.Id);
        reloaded.Role.Should().Be(AppRoles.USER);
    }
}
