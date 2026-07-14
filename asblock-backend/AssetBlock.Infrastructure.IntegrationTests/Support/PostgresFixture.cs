using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Shared PostgreSQL Testcontainers instance for Infrastructure store/migration tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().Build();

    private string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Creates a context against a wiped schema with real EF migrations applied.
    /// Uses DROP SCHEMA instead of EnsureDeleted to avoid "cannot drop the currently open database"
    /// when pooled connections still reference the Testcontainers database.
    /// </summary>
    public async Task<ApplicationDbContext> CreateCleanDbContext(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection.ClearAllPools();

        await using (var setup = CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
                """,
                cancellationToken);

            await setup.Database.MigrateAsync(cancellationToken);
        }

        NpgsqlConnection.ClearAllPools();
        return CreateDbContext();
    }

    public ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new ApplicationDbContext(options);
    }
}
