using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AssetBlock.SearchEvaluation.Infrastructure;

public sealed record SystemEnvironmentProvenance(
    string GitCommit,
    string OperatingSystem,
    string Architecture,
    int CpuCores,
    long TotalRamBytes,
    string ContainerImageDigest,
    string PostgresVersion,
    string PgVectorVersion,
    string HnswState,
    bool IsGitDirty = false,
    string? SourceFingerprint = null);

public sealed class SearchEvaluationDbFixture : IAsyncDisposable
{
    private const string PGVECTOR_IMAGE_DIGEST = "pgvector/pgvector:0.8.6-pg16-bookworm@sha256:ccc6e83d6e35e931dc7c5def2022729d5a6c370318d099181995567ff1fb4d6b";

    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(2);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PGVECTOR_IMAGE_DIGEST).Build();
    private NpgsqlDataSource? _dataSource;
    private bool _initialized;

    public string ConnectionString => _postgres.GetConnectionString();

    public NpgsqlConnection CreateConnection()
    {
        if (_dataSource == null)
        {
            var builder = new NpgsqlDataSourceBuilder(ConnectionString);
            builder.UseVector();
            _dataSource = builder.Build();
        }
        return _dataSource.CreateConnection();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_startTimeout);

        try
        {
            await _postgres.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"PostgreSQL pgvector container failed to start within {_startTimeout.TotalSeconds:0}s. " +
                "Ensure Docker Desktop is running.");
        }

        NpgsqlConnection.ClearAllPools();

        await using (ApplicationDbContext setup = CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
                CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;
                """,
                cancellationToken);

            await setup.Database.MigrateAsync(cancellationToken);
        }

        _initialized = true;
    }

    public ApplicationDbContext CreateDbContext()
    {
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System));

        return new ApplicationDbContext(optionsBuilder.Options);
    }

    public async Task<SystemEnvironmentProvenance> CollectProvenance(CancellationToken cancellationToken = default)
    {
        var gitCommit = ResolveGitCommit();
        var osDesc = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        var cpuCount = Environment.ProcessorCount;
        var ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        string pgVersion;
        string pgVectorVersion;
        string hnswState;

        await using (ApplicationDbContext db = CreateDbContext())
        {
            pgVersion = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").FirstAsync(cancellationToken);
            pgVectorVersion = await db.Database.SqlQueryRaw<string>(
                "SELECT extversion AS \"Value\" FROM pg_extension WHERE extname = 'vector'").FirstOrDefaultAsync(cancellationToken) ?? "unknown";

            List<string> hnswIndexes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE tablename = 'asset_embeddings' AND indexdef ILIKE '%hnsw%'
                """).ToListAsync(cancellationToken);

            hnswState = hnswIndexes.Count == 0 ? "absent" : $"present ({string.Join(", ", hnswIndexes)})";
        }

        var isDirty = ResolveGitIsDirty();
        if (gitCommit == "unknown-commit" || string.IsNullOrWhiteSpace(gitCommit))
        {
            gitCommit = "unknown";
        }
        var sourceFingerprint = ComputeSourceFingerprint();

        return new SystemEnvironmentProvenance(
            gitCommit,
            osDesc,
            arch,
            cpuCount,
            ramBytes,
            PGVECTOR_IMAGE_DIGEST,
            pgVersion,
            pgVectorVersion,
            hnswState,
            isDirty,
            sourceFingerprint);
    }

    public static string ComputeSourceFingerprint(string? baseDirectory = null)
    {
        try
        {
            var root = baseDirectory ?? ResolveBackendSourceRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return "unknown";
            }

            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".json", ".csproj", ".sql"
            };

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
                .Where(f =>
                {
                    var normalized = f.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var sep = Path.DirectorySeparatorChar;
                    return !normalized.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}artifacts{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}.vs{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}.idea{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}node_modules{sep}", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains($"{sep}TestResults{sep}", StringComparison.OrdinalIgnoreCase);
                })
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                return "unknown";
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var relPath in files)
            {
                var fullPath = Path.Combine(root, relPath);
                sha.AppendData(Encoding.UTF8.GetBytes(relPath));
                sha.AppendData(File.ReadAllBytes(fullPath));
            }

            var hashHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            var rawCommit = ResolveGitCommit();
            var commitPrefix = (rawCommit == "unknown-commit" || string.IsNullOrWhiteSpace(rawCommit)) ? "unknown" : rawCommit;

            return $"{commitPrefix}-src:{hashHex[..16]}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveBackendSourceRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, "asblock-backend");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(current, "AGENTS.md")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return AppContext.BaseDirectory;
    }

    private static bool ResolveGitIsDirty()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                return !string.IsNullOrWhiteSpace(output);
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static string ResolveGitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var commit = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(commit) && commit.Length >= 7)
                {
                    return commit;
                }
            }
        }
        catch
        {
            // fallback if git command line not in path
        }

        return "unknown-commit";
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }
        NpgsqlConnection.ClearAllPools();
        await _postgres.DisposeAsync();
    }
}
