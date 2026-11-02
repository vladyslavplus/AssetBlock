using System.Diagnostics.CodeAnalysis;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Shared PostgreSQL instance for infrastructure store tests. A loopback-only task database can
/// replace Testcontainers when Docker is unavailable; each fixture owns a separate database.
/// Migrations run once into a template database; every clean context is a copy of that template.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(2);

    private PostgreSqlContainer? _postgres;
    private string? _adminConnectionString;
    private string? _templateDatabaseName;
    private string? _currentDatabaseName;

    private string AdminConnectionString => _adminConnectionString
        ?? throw new InvalidOperationException("PostgreSQL fixture has not been initialized.");

    private string TemplateDatabase => _templateDatabaseName
        ?? throw new InvalidOperationException("PostgreSQL fixture has not been initialized.");

    private string CurrentDatabase => _currentDatabaseName
        ?? throw new InvalidOperationException("PostgreSQL fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        var externalConnectionString = Environment.GetEnvironmentVariable("ASBLOCK_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(externalConnectionString))
        {
            _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.6-pg16-bookworm@sha256:ccc6e83d6e35e931dc7c5def2022729d5a6c370318d099181995567ff1fb4d6b")
                .WithLabel("ao.session", Environment.GetEnvironmentVariable("AO_SESSION_ID") ?? "local")
                .Build();
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

            _adminConnectionString = WithDatabase(_postgres.GetConnectionString(), "postgres");
        }
        else
        {
            var supplied = new NpgsqlConnectionStringBuilder(externalConnectionString);
            if (supplied.Host is not ("127.0.0.1" or "::1" or "localhost"))
            {
                throw new InvalidOperationException("ASBLOCK_TEST_POSTGRES must target loopback.");
            }

            _adminConnectionString = WithDatabase(supplied.ConnectionString, "postgres");
        }

        await CreateTemplateDatabaseAsync();
        _currentDatabaseName = await CloneTemplateAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_currentDatabaseName is not null)
            {
                await DropDatabaseAsync(_currentDatabaseName);
            }

            if (_templateDatabaseName is not null)
            {
                await DropDatabaseAsync(_templateDatabaseName);
            }
        }
        finally
        {
            if (_postgres is not null)
            {
                await _postgres.DisposeAsync();
            }
        }
    }

    public ApplicationDbContext CreateDbContext(
        Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null)
    {
        return CreateContext(ConnectionStringFor(CurrentDatabase), configure);
    }

    /// <summary>
    /// Creates a context against a fresh copy of the migrated template database. The previous
    /// test database is dropped, so contexts created before this call must not be used afterwards.
    /// </summary>
    public async Task<ApplicationDbContext> CreateCleanDbContext(
        Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var previousDatabase = CurrentDatabase;
        _currentDatabaseName = await CloneTemplateAsync(cancellationToken);
        await DropDatabaseAsync(previousDatabase, cancellationToken);
        return CreateDbContext(configure);
    }

    private async Task CreateTemplateDatabaseAsync()
    {
        var template = $"asblock_tmpl_{Guid.NewGuid():N}";
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(template)}");
        _templateDatabaseName = template;

        await using (ApplicationDbContext migrate = CreateContext(ConnectionStringFor(template, pooling: false)))
        {
            await migrate.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public");
            await migrate.Database.MigrateAsync();
        }

        // CREATE DATABASE ... TEMPLATE fails while any session is connected to the template.
        await ExecuteAdminAsync($"ALTER DATABASE {Quote(template)} WITH ALLOW_CONNECTIONS false");
    }

    private async Task<string> CloneTemplateAsync(CancellationToken cancellationToken = default)
    {
        var database = $"asblock_t_{Guid.NewGuid():N}";
        await ExecuteAdminAsync(
            $"CREATE DATABASE {Quote(database)} TEMPLATE {Quote(TemplateDatabase)}",
            cancellationToken);
        return database;
    }

    private async Task DropDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        // FORCE terminates leftover pooled sessions of the dropped database.
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)", cancellationToken);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Database names are generated by this fixture and quoted with NpgsqlCommandBuilder.")]
    private async Task ExecuteAdminAsync(string commandText, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection admin = new(AdminConnectionString);
        await admin.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = admin.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ConnectionStringFor(string database, bool pooling = true) =>
        WithDatabase(AdminConnectionString, database, pooling);

    private static string WithDatabase(string connectionString, string database, bool pooling = true) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database, Pooling = pooling }.ConnectionString;

    private static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private static ApplicationDbContext CreateContext(
        string connectionString,
        Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null)
    {
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AssetBlock.Infrastructure.Persistence.Interceptors.AuditTimestampsInterceptor(TimeProvider.System));
        configure?.Invoke(optionsBuilder);
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
