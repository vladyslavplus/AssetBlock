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
    private static readonly HashSet<string> _postgresScopeCheckConstraints =
    [
        "CK_asset_draft_workspaces_version_scope_key",
        "CK_asset_material_metadata_revisions_revision_scope",
        "CK_asset_source_declaration_revisions_revision_scope",
        "CK_asset_seller_evidence_revisions_revision_scope"
    ];

    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        var scopeChecks = operation.CheckConstraints
            .Where(c => _postgresScopeCheckConstraints.Contains(c.Name))
            .ToList();
        foreach (AddCheckConstraintOperation constraint in scopeChecks)
        {
            operation.CheckConstraints.Remove(constraint);
        }

        // Text constraint fidelity belongs to the PostgreSQL response/disposition tests.
        foreach (AddCheckConstraintOperation constraint in operation.CheckConstraints
            .Where(c => c.Name is "CK_seller_responses_text" or "CK_finding_dispositions_rationale").ToList())
        {
            operation.CheckConstraints.Remove(constraint);
        }

        if (operation.Name == "asset_processing_jobs")
        {
            var targetConstraints = operation.CheckConstraints
                .Where(c => c.Name is "CK_asset_processing_jobs_error_code"
                    or "CK_asset_processing_jobs_embedding_hashes"
                    or "CK_asset_processing_jobs_transport_lease_token")
                .ToList();

            foreach (AddCheckConstraintOperation constraint in targetConstraints)
            {
                operation.CheckConstraints.Remove(constraint);
            }
        }

        // The analysis and security evidence tables are guarded by PostgreSQL regex operators and
        // jsonb functions. Their fidelity is covered by the PostgreSQL integration suite, so the
        // unsupported constraints are stripped here instead of being emulated.
        if (operation.Name is "code_analysis_policy_versions"
            or "code_analysis_report_headers"
            or "code_analysis_report_stages"
            or "code_analysis_report_findings"
            or "code_analysis_report_admissions"
            or "code_analysis_reuse_artifacts"
            or "code_analysis_runs"
            or "security_rechecks"
            or "security_evidence_records"
            or "analysis_capacity_slots"
            or "analysis_service_registrations")
        {
            foreach (AddCheckConstraintOperation constraint in operation.CheckConstraints.ToList())
            {
                operation.CheckConstraints.Remove(constraint);
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

            foreach (AddCheckConstraintOperation constraint in targetConstraints)
            {
                operation.CheckConstraints.Remove(constraint);
            }
        }

        if (operation.Name == "asset_embeddings")
        {
            var targetConstraints = operation.CheckConstraints
                .Where(c => c.Name is "CK_asset_embeddings_model_key"
                    or "CK_asset_embeddings_content_hash"
                    or "CK_asset_embeddings_model_digest"
                    or "CK_asset_embeddings_vector_dims")
                .ToList();

            foreach (AddCheckConstraintOperation constraint in targetConstraints)
            {
                operation.CheckConstraints.Remove(constraint);
            }
        }

        base.Generate(operation, model, builder, terminate);
    }
}

