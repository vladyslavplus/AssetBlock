using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Interceptors;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AssetBlock.Infrastructure.Tests.Persistence;

public sealed class AuditTimestampsInterceptorTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestTimeProvider _timeProvider;
    private readonly ApplicationDbContext _db;

    public AuditTimestampsInterceptorTests()
    {
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _connection.CreateFunction("jsonb_typeof", (string _) => "object");
        _connection.CreateFunction("octet_length", (string _) => 1);

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IMigrationsSqlGenerator, SqliteTestMigrationsSqlGenerator>()
            .AddInterceptors(new AuditTimestampsInterceptor(_timeProvider))
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Added_entity_without_CreatedAt_gets_timestamp_from_TimeProvider()
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "3D Models",
            Slug = "3d-models",
            Description = "All 3D assets"
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        category.CreatedAt.Should().Be(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Added_entity_with_explicit_CreatedAt_preserves_caller_timestamp()
    {
        DateTimeOffset explicitTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Audio",
            Slug = "audio",
            Description = "Sound effects",
            CreatedAt = explicitTime
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        category.CreatedAt.Should().Be(explicitTime);
    }

    [Fact]
    public async Task Modified_entity_sets_UpdatedAt_from_TimeProvider()
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Textures",
            Slug = "textures",
            Description = "PBR Materials"
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        category.UpdatedAt.Should().BeNull();

        DateTimeOffset updatedTime = new(2026, 9, 5, 8, 30, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(updatedTime);

        category.Description = "Updated PBR Materials";
        await _db.SaveChangesAsync();

        category.UpdatedAt.Should().Be(updatedTime);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
