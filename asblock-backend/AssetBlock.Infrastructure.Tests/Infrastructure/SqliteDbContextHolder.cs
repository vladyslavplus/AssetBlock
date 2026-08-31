using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AssetBlock.Infrastructure.Tests.Infrastructure;

/// <summary>
/// SQLite in-memory database (shared connection) — supports ExecuteDeleteAsync used by several stores.
/// </summary>
internal sealed class SqliteDbContextHolder : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public ApplicationDbContext Context { get; }

    public SqliteDbContextHolder()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _connection.CreateFunction("jsonb_typeof", (string _) => "object");
        _connection.CreateFunction("octet_length", (string _) => 1);
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IMigrationsSqlGenerator, SqliteTestMigrationsSqlGenerator>()
            .Options;
        Context = new ApplicationDbContext(options);
        Context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class SqliteTestMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider relationalAnnotationProvider)
    : SqliteMigrationsSqlGenerator(dependencies, relationalAnnotationProvider)
{
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        if (operation.Name == "asset_processing_jobs")
        {
            AddCheckConstraintOperation? targetConstraint = operation.CheckConstraints
                .FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_error_code");

            if (targetConstraint is not null)
            {
                operation.CheckConstraints.Remove(targetConstraint);
            }
        }

        if (operation.Name == "asset_versions")
        {
            AddCheckConstraintOperation? targetConstraint = operation.CheckConstraints
                .FirstOrDefault(c => c.Name == "CK_asset_versions_processing_error_code");

            if (targetConstraint is not null)
            {
                operation.CheckConstraints.Remove(targetConstraint);
            }
        }

        if (operation.Name == "asset_listing_suggestions")
        {
            var targetConstraints = operation.CheckConstraints
                .Where(c => c.Name is "CK_asset_listing_suggestions_content_hash"
                    or AssetListingSuggestionConfiguration.CK_TAGS_TYPE
                    or AssetListingSuggestionConfiguration.CK_TAGS_LENGTH
                    or AssetListingSuggestionConfiguration.CK_TAGS_ITEMS
                    or AssetListingSuggestionConfiguration.CK_TAGS_SIZE)
                .ToList();

            foreach (AddCheckConstraintOperation? constraint in targetConstraints)
            {
                operation.CheckConstraints.Remove(constraint);
            }
        }

        base.Generate(operation, model, builder, terminate);
    }
}

