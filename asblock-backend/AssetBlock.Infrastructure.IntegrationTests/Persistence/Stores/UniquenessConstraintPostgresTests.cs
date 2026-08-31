using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class UniquenessConstraintPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UserStore_Create_WhenEmailAlreadyExists_ShouldThrowDuplicateEmailException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new UserStore(db);
        await store.Create("first", "dup@example.test", "hash-a");

        Func<Task<User>> act = async () => await store.Create("second", "dup@example.test", "hash-b");

        await act.Should().ThrowAsync<DuplicateEmailException>();
    }

    [Fact]
    public async Task UserStore_Create_WhenUsernameAlreadyExists_ShouldThrowDuplicateUsernameException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new UserStore(db);
        await store.Create("taken", "first@example.test", "hash-a");

        Func<Task<User>> act = async () => await store.Create("taken", "second@example.test", "hash-b");

        await act.Should().ThrowAsync<DuplicateUsernameException>();
    }

    [Fact]
    public async Task UserStore_Update_WhenUsernameAlreadyExists_ShouldThrowDuplicateUsernameException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new UserStore(db);
        await store.Create("taken", "a@example.test", "hash-a");
        User other = await store.Create("free", "b@example.test", "hash-b");

        other.Username = "taken";
        Func<Task<User>> act = async () => await store.Update(other);

        await act.Should().ThrowAsync<DuplicateUsernameException>();
    }

    [Fact]
    public async Task TagStore_Add_WhenNameAlreadyExists_ShouldThrowDuplicateTagNameException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new TagStore(db);
        await store.Add(TestData.CreateTag("existing"));

        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        var store2 = new TagStore(db2);
        Func<Task<Tag>> act = async () => await store2.Add(TestData.CreateTag("existing"));

        await act.Should().ThrowAsync<DuplicateTagNameException>();
    }

    [Fact]
    public async Task TagStore_Update_WhenNameAlreadyExists_ShouldThrowDuplicateTagNameException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new TagStore(db);
        Tag existing = TestData.CreateTag("existing");
        Tag renaming = TestData.CreateTag("renaming");
        await store.Add(existing);
        await store.Add(renaming);

        renaming.Name = existing.Name;
        Func<Task<Tag>> act = async () => await store.Update(renaming);

        await act.Should().ThrowAsync<DuplicateTagNameException>();
    }
}
