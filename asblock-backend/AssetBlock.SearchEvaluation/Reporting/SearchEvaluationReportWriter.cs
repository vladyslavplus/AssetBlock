using System.Globalization;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation.Infrastructure;

namespace AssetBlock.SearchEvaluation.Reporting;

public sealed record PathBenchmarkMetrics(
    string PathName,
    int SampleCount,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double MinMs,
    double MaxMs,
    double? TargetP95Ms,
    bool? TargetPassed);

public sealed record ConcurrencyBenchmarkResult(
    int Concurrency,
    List<PathBenchmarkMetrics> PathMetrics);

public sealed record CorpusBenchmarkResult(
    int CorpusSize,
    int WarmupCount,
    int SampleCount,
    List<ConcurrencyBenchmarkResult> ConcurrencyResults);

public sealed record BenchmarkReportData(
    SystemEnvironmentProvenance Provenance,
    EmbeddingOptions PinnedModel,
    List<CorpusBenchmarkResult> CorpusResults,
    bool ExactScanPassed,
    string Conclusion,
    bool IsPrescribedDecisionProtocol = true);

public sealed record BackfillEvidenceReportData(
    SystemEnvironmentProvenance Provenance,
    EmbeddingOptions PinnedModel,
    int TotalAssetsEvaluated,
    int CompletedCount,
    int PendingCount,
    int FailedCount,
    int StaleOrRejectedCount,
    bool AdvisoryLockVerified,
    bool CursorProgressionVerified,
    string Summary);

public sealed record QualityEvaluationReportData(
    SystemEnvironmentProvenance Provenance,
    EmbeddingOptions PinnedModel,
    int QueryCount,
    string AdjudicationVersion,
    string AdjudicationDate,
    Metrics.MacroMetricsSummary LexicalSummary,
    Metrics.MacroMetricsSummary HybridSummary,
    List<Evaluation.QualityGateResult> QualityGates,
    bool AllGatesPassed);

public static class SearchEvaluationReportWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string EnsureReportDirectory()
    {
        var root = ResolveRepositoryRoot();
        var reportDir = Path.Combine(root, "artifacts", "search-evaluation");
        if (!Directory.Exists(reportDir))
        {
            Directory.CreateDirectory(reportDir);
        }

        return reportDir;
    }

    public static (string JsonPath, string MarkdownPath) WriteBenchmarkReport(BenchmarkReportData data)
    {
        var reportDir = EnsureReportDirectory();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(reportDir, $"benchmark-evidence-{timestamp}.json");
        var mdPath = Path.Combine(reportDir, $"benchmark-evidence-{timestamp}.md");

        var json = JsonSerializer.Serialize(data, _jsonOptions);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);

        var md = GenerateBenchmarkMarkdown(data);
        File.WriteAllText(mdPath, md, Encoding.UTF8);

        return (jsonPath, mdPath);
    }

    public static (string JsonPath, string MarkdownPath) WriteBackfillReport(BackfillEvidenceReportData data)
    {
        var reportDir = EnsureReportDirectory();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(reportDir, $"backfill-evidence-{timestamp}.json");
        var mdPath = Path.Combine(reportDir, $"backfill-evidence-{timestamp}.md");

        var json = JsonSerializer.Serialize(data, _jsonOptions);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);

        var md = GenerateBackfillMarkdown(data);
        File.WriteAllText(mdPath, md, Encoding.UTF8);

        return (jsonPath, mdPath);
    }

    public static (string JsonPath, string MarkdownPath) WriteQualityEvaluationReport(QualityEvaluationReportData data)
    {
        var reportDir = EnsureReportDirectory();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(reportDir, $"quality-evaluation-{timestamp}.json");
        var mdPath = Path.Combine(reportDir, $"quality-evaluation-{timestamp}.md");

        var json = JsonSerializer.Serialize(data, _jsonOptions);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);

        var md = GenerateQualityEvaluationMarkdown(data);
        File.WriteAllText(mdPath, md, Encoding.UTF8);

        return (jsonPath, mdPath);
    }

    private static string GenerateQualityEvaluationMarkdown(QualityEvaluationReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AssetBlock Search Quality Evaluation Report");
        sb.AppendLine();
        sb.AppendLine("## Provenance & Metadata");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Git Commit:** `{data.Provenance.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **OS / Architecture:** {data.Provenance.OperatingSystem} ({data.Provenance.Architecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **PostgreSQL Version:** {data.Provenance.PostgresVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **pgvector Version:** {data.Provenance.PgVectorVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Container Digest:** `{data.Provenance.ContainerImageDigest}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Pinned Model:** `{data.PinnedModel.Model}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Model Revision:** `{data.PinnedModel.Revision}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Model Digest:** `{data.PinnedModel.Digest}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Embedding Dimension:** {data.PinnedModel.Dimension}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Adjudication Version:** `{data.AdjudicationVersion}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Adjudication Date:** `{data.AdjudicationDate}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Total Adjudicated Queries:** {data.QueryCount}");
        sb.AppendLine();
        sb.AppendLine("## Macro Metrics Summary");
        sb.AppendLine("| Metric | Lexical Search | Hybrid Retrieval | Delta |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Macro nDCG@10 | {data.LexicalSummary.MeanNdcgAt10:F4} | {data.HybridSummary.MeanNdcgAt10:F4} | {data.HybridSummary.MeanNdcgAt10 - data.LexicalSummary.MeanNdcgAt10:+0.0000;-0.0000;0.0000} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Macro Recall@20 | {data.LexicalSummary.MeanRecallAt20:F4} | {data.HybridSummary.MeanRecallAt20:F4} | {data.HybridSummary.MeanRecallAt20 - data.LexicalSummary.MeanRecallAt20:+0.0000;-0.0000;0.0000} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Macro MRR | {data.LexicalSummary.MeanMrr:F4} | {data.HybridSummary.MeanMrr:F4} | {data.HybridSummary.MeanMrr - data.LexicalSummary.MeanMrr:+0.0000;-0.0000;0.0000} |");
        sb.AppendLine();
        sb.AppendLine("## Release Quality Gates");
        sb.AppendLine("| Gate Name | Scope | Requirement | Lexical | Hybrid | Delta | Verdict |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (Evaluation.QualityGateResult gate in data.QualityGates)
        {
            var verdictStr = gate.Passed ? "**PASS**" : "**FAIL**";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {gate.Name} | {gate.Scope} | `{gate.Requirement}` | {gate.LexicalValue:F4} | {gate.HybridValue:F4} | {gate.Delta:+0.0000;-0.0000;0.0000} | {verdictStr} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Final Decision");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Overall Quality Verdict:** {(data.AllGatesPassed ? "**PASSED**" : "**BLOCKED / FAILED**")}");
        sb.AppendLine();
        sb.AppendLine("> Note: Aggregate metrics only. No query text, document keys, personal judgments, or vectors are exposed.");

        return sb.ToString();
    }

    private static string GenerateBenchmarkMarkdown(BenchmarkReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AssetBlock Exact-Search Benchmark Evidence");
        sb.AppendLine();
        sb.AppendLine("## System & Model Provenance");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Git Commit:** `{data.Provenance.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **OS / Architecture:** {data.Provenance.OperatingSystem} ({data.Provenance.Architecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **CPU Cores:** {data.Provenance.CpuCores}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **RAM:** {data.Provenance.TotalRamBytes / (1024 * 1024 * 1024.0):F2} GB");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **PostgreSQL Version:** {data.Provenance.PostgresVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **pgvector Version:** {data.Provenance.PgVectorVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **pgvector Container Digest:** `{data.Provenance.ContainerImageDigest}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **HNSW Index State:** `{data.Provenance.HnswState}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Pinned Model:** `{data.PinnedModel.Model}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Model Revision:** `{data.PinnedModel.Revision}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Model Digest:** `{data.PinnedModel.Digest}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Embedding Dimension:** {data.PinnedModel.Dimension}");
        sb.AppendLine();
        sb.AppendLine("## Benchmark Results");

        foreach (CorpusBenchmarkResult corpus in data.CorpusResults)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Corpus Size: {corpus.CorpusSize:N0} documents (Warmup: {corpus.WarmupCount}, Samples: {corpus.SampleCount})");
            sb.AppendLine();

            foreach (ConcurrencyBenchmarkResult concurrency in corpus.ConcurrencyResults)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"#### Concurrency Level: {concurrency.Concurrency}");
                sb.AppendLine();
                sb.AppendLine("| Measurement Path | Samples | Mean (ms) | p50 (ms) | p95 (ms) | Min (ms) | Max (ms) | Target p95 (ms) | Target Status |");
                sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

                foreach (PathBenchmarkMetrics metric in concurrency.PathMetrics)
                {
                    var targetStr = metric.TargetP95Ms.HasValue ? string.Create(CultureInfo.InvariantCulture, $"<= {metric.TargetP95Ms.Value:F0}") : "N/A";
                    var statusStr = metric.TargetPassed.HasValue
                        ? (metric.TargetPassed.Value ? "PASS" : "FAIL")
                        : "RECORDED";

                    sb.AppendLine(CultureInfo.InvariantCulture, $"| {metric.PathName} | {metric.SampleCount} | {metric.MeanMs:F2} | {metric.P50Ms:F2} | {metric.P95Ms:F2} | {metric.MinMs:F2} | {metric.MaxMs:F2} | {targetStr} | {statusStr} |");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Benchmark Conclusion");
        if (!data.IsPrescribedDecisionProtocol)
        {
            sb.AppendLine("- **Run Type:** `EXPLORATORY RUN (Non-prescribed parameters)`");
            sb.AppendLine("- **Exact Scan Rollout Verdict:** `BLOCKED (Rollout decision not permitted)`");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Exact Scan Verdict:** {(data.ExactScanPassed ? "**PASSED**" : "**FAILED**")}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Summary:** {data.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("> Note: All benchmark measurements were performed against isolated disposable databases with zero production data access. No identifiers or query texts are included in this report.");

        return sb.ToString();
    }

    private static string GenerateBackfillMarkdown(BackfillEvidenceReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AssetBlock Durable Backfill Evidence");
        sb.AppendLine();
        sb.AppendLine("## System & Model Provenance");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Git Commit:** `{data.Provenance.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **OS / Architecture:** {data.Provenance.OperatingSystem} ({data.Provenance.Architecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **PostgreSQL / pgvector:** {data.Provenance.PostgresVersion} (pgvector {data.Provenance.PgVectorVersion})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Container Digest:** `{data.Provenance.ContainerImageDigest}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Pinned Model:** `{data.PinnedModel.Model}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Model Digest:** `{data.PinnedModel.Digest}`");
        sb.AppendLine();
        sb.AppendLine("## Aggregate Backfill Verification");
        sb.AppendLine("| Metric Category | Aggregate Count |");
        sb.AppendLine("| :--- | :---: |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Total Assets Evaluated | {data.TotalAssetsEvaluated:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Completed Work Items | {data.CompletedCount:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Pending / Queued Work Items | {data.PendingCount:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Failed Work Items (Recorded) | {data.FailedCount:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Stale or Ineligible Assets (Skipped/Rejected) | {data.StaleOrRejectedCount:N0} |");
        sb.AppendLine();
        sb.AppendLine("## Invariant Verification");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Advisory Lock Concurrency Gate:** {(data.AdvisoryLockVerified ? "VERIFIED (Concurrent cycle rejected)" : "FAILED")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Durable Cursor Progression & Wrap:** {(data.CursorProgressionVerified ? "VERIFIED" : "FAILED")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **HNSW State:** `{data.Provenance.HnswState}`");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine(data.Summary);
        sb.AppendLine();
        sb.AppendLine("> Note: Aggregate counts only. No asset IDs, job payloads, raw text, hashes, vectors, cache keys, or user IDs are exposed.");

        return sb.ToString();
    }

    private static string ResolveRepositoryRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "AGENTS.md")) || Directory.Exists(Path.Combine(current, ".git")))
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

        return Directory.GetCurrentDirectory();
    }
}
