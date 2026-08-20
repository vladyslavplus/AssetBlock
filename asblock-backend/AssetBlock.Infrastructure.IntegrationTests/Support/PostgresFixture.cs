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
    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(2);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16.14-alpine3.24").Build();

    private string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(_startTimeout);
        try
        {
            await _postgres.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"PostgreSQL Testcontainers failed to start within {_startTimeout.TotalSeconds:0}s. " +
                "Check Docker Desktop is running and not wedged (restart if containers stay in Created).");
        }
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _postgres.DisposeAsync();
    }

    public ApplicationDbContext CreateDbContext(
        Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString);
        configure?.Invoke(optionsBuilder);
        return new ApplicationDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Creates a context against a wiped schema with the full EF migration history applied.
    /// </summary>
    public async Task<ApplicationDbContext> CreateCleanDbContext(
        Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null,
        CancellationToken cancellationToken = default)
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
        return CreateDbContext(configure);
    }
}
