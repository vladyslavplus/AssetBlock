using Npgsql;
using Testcontainers.PostgreSql;

namespace AssetBlock.WebApi.IntegrationTests.Support;

public sealed class IntegrationTestFixture : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(2);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.6-pg16-bookworm@sha256:ccc6e83d6e35e931dc7c5def2022729d5a6c370318d099181995567ff1fb4d6b").Build();

    private AssetBlockWebApplicationFactory? _factory;

    public AssetBlockWebApplicationFactory Factory => _factory!;

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(_startTimeout);
        try
        {
            await _postgres.StartAsync(cts.Token);
            await _postgres.ExecScriptAsync("CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;", cts.Token);
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

        _factory = new AssetBlockWebApplicationFactory(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }

    /// <summary>Creates an empty migrated-on-first-use database on the shared container, for tests that leave many cases behind.</summary>
    public async Task<string> CreateIsolatedDatabaseAsync(string databaseName)
    {
        await _postgres.ExecScriptAsync($"CREATE DATABASE \"{databaseName}\";");
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = databaseName };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;", connection);
        await extension.ExecuteNonQueryAsync();
        return builder.ConnectionString;
    }

    public Task DropIsolatedDatabaseAsync(string databaseName) =>
        _postgres.ExecScriptAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
}
