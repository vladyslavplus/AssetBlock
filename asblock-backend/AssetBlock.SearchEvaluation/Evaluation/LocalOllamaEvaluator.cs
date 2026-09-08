using System.Diagnostics;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Ollama;
using AssetBlock.SearchEvaluation.Reporting;
using AssetBlock.SearchEvaluation.Validation;
using Pgvector;

namespace AssetBlock.SearchEvaluation.Evaluation;

public static class LocalOllamaEvaluator
{
    public static Task<int> RunEvaluationAsync(
        DatasetV1Dto dataset,
        EmbeddingOptions options,
        IOllamaEmbeddingClient client,
        CancellationToken cancellationToken = default)
    {
        return RunEvaluationAsync(dataset, qrelsPath: null, options, client, cancellationToken);
    }

    public static async Task<int> RunEvaluationAsync(
        DatasetV1Dto dataset,
        string? qrelsPath,
        EmbeddingOptions options,
        IOllamaEmbeddingClient client,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine(" Candidate Model Provenance");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Provider:          {options.Provider}");
        Console.WriteLine($"Model:             {options.Model}");
        Console.WriteLine($"Revision:          {options.Revision}");
        Console.WriteLine($"Digest:            {options.Digest}");
        Console.WriteLine($"Dimension:         {options.Dimension}");
        Console.WriteLine($"Base URL:          {options.BaseUrl}");
        Console.WriteLine($"Timeout:           {options.RequestTimeoutSeconds}s");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine();

        // 1. Enforce adjudicated human qrels prerequisite
        if (string.IsNullOrWhiteSpace(qrelsPath) || !File.Exists(qrelsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[BLOCKED / HUMAN RELEVANCE JUDGMENTS REQUIRED]");
            Console.WriteLine("Release-quality evaluation requires independent, adjudicated human relevance judgments (qrels).");
            Console.WriteLine("Tracked synthetic fixtures cannot be used for release-quality evaluation.");
            if (string.IsNullOrWhiteSpace(qrelsPath))
            {
                Console.WriteLine("No qrels file was specified. Pass --qrels <path-to-human-qrels.json>.");
            }
            else
            {
                Console.WriteLine($"Qrels file not found at: {qrelsPath}");
            }
            Console.WriteLine();
            Console.WriteLine("Prerequisites for release-quality evaluation:");
            Console.WriteLine("  1. Independent human relevance judgments adhering to qrels.schema.json.");
            Console.WriteLine("  2. Provenance must be 'human-adjudicated' (not synthetic-fixtures).");
            Console.WriteLine("  3. Judgments must NOT contain reviewer identities or personal data.");
            Console.WriteLine("  4. At least one query with relevance grade >= 2.");
            Console.WriteLine("No judgments are fabricated, relabeled, or deterministically derived.");
            Console.ResetColor();
            return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
        }

        Console.WriteLine($"--> Validating human-adjudicated qrels file: {qrelsPath}...");
        QrelsValidationResult qrelsValidation = QrelsValidator.ValidateFile(qrelsPath, dataset);
        if (!qrelsValidation.IsValid || qrelsValidation.Qrels == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[BLOCKED / QRELS VALIDATION FAILED]");
            Console.WriteLine("Provided qrels file failed validation:");
            foreach (var err in qrelsValidation.Errors)
            {
                Console.WriteLine($"  - {err}");
            }
            Console.WriteLine();
            Console.WriteLine("Release-quality evaluation remains blocked until valid human-adjudicated qrels are provided.");
            Console.ResetColor();
            return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
        }

        QrelsV1Dto qrels = qrelsValidation.Qrels;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Validated human-adjudicated qrels: {qrels.Queries.Count} queries (Adjudication: {qrels.AdjudicationVersion}, Date: {qrels.AdjudicationDate}).");
        Console.ResetColor();
        Console.WriteLine();

        // 2. Verify model availability in local Ollama daemon
        Console.WriteLine($"--> Verifying local availability of candidate model '{options.Model}'...");
        ModelVerificationResult verification = await client.CheckModelAvailability(cancellationToken);
        if (!verification.IsAvailable)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[BLOCKED / MANUAL EVALUATION REQUIRED]");
            Console.WriteLine(verification.ErrorMessage);
            Console.WriteLine();
            Console.WriteLine("Prerequisites for local model evaluation:");
            Console.WriteLine($"  1. Local Ollama daemon running on loopback ({options.BaseUrl}).");
            Console.WriteLine($"  2. Installed candidate model with pinned non-floating tag ({options.Model}).");
            Console.WriteLine($"  3. Exact model revision string ({options.Revision}) and SHA-256 digest ({options.Digest}).");
            Console.WriteLine($"  4. Positive embedding dimension ({options.Dimension}).");
            Console.WriteLine("  5. Independent human-reviewed relevance judgments.");
            Console.WriteLine("No models are pulled or downloaded automatically.");
            Console.ResetColor();
            return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Candidate model verified in local Ollama daemon.");
        if (!string.IsNullOrWhiteSpace(verification.ActualDigest))
        {
            Console.WriteLine($"  Verified digest: {verification.ActualDigest}");
        }
        Console.ResetColor();
        Console.WriteLine();

        // 3. Initialize isolated disposable pgvector Testcontainer
        Console.WriteLine("--> Initializing isolated disposable pgvector Testcontainer...");
        await using var fixture = new SearchEvaluationDbFixture();
        await fixture.InitializeAsync(cancellationToken);

        SystemEnvironmentProvenance provenance = await fixture.CollectProvenance(cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Isolated PostgreSQL container initialized (pgvector: {provenance.PgVectorVersion}, HNSW: {provenance.HnswState}).");
        Console.ResetColor();
        Console.WriteLine();

        var modelKey = EmbeddingModelKey.Compute(options);

        // 4. Seed dataset into disposable database
        Console.WriteLine($"--> Seeding {dataset.Documents.Count} documents into isolated database...");
        var docIdMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var idToDocMap = new Dictionary<Guid, string>();

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var author = new User
            {
                Id = Guid.NewGuid(),
                Username = "eval_author",
                Email = "eval_author@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(author);
            Guid authorId = author.Id;

            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Seed unique categories and tags from dataset documents
            var categoryLookup = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            var tagLookup = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);

            foreach (DatasetDocumentDto doc in dataset.Documents)
            {
                var catName = string.IsNullOrWhiteSpace(doc.Category) ? "General" : doc.Category.Trim();
                if (!categoryLookup.TryGetValue(catName, out Category? cat))
                {
                    cat = new Category
                    {
                        Id = Guid.NewGuid(),
                        Name = catName,
                        Slug = catName.ToLowerInvariant().Replace(' ', '-'),
                        CreatedAt = now
                    };
                    categoryLookup[catName] = cat;
                    db.Categories.Add(cat);
                }

                foreach (var tagName in doc.Tags)
                {
                    var cleanTag = tagName.Trim();
                    if (!string.IsNullOrEmpty(cleanTag) && !tagLookup.TryGetValue(cleanTag, out Tag? tag))
                    {
                        tag = new Tag
                        {
                            Id = Guid.NewGuid(),
                            Name = cleanTag
                        };
                        tagLookup[cleanTag] = tag;
                        db.Tags.Add(tag);
                    }
                }
            }

            var docStopwatch = new Stopwatch();
            var docLatencies = new List<double>();

            foreach (DatasetDocumentDto doc in dataset.Documents)
            {
                var assetId = Guid.NewGuid();
                docIdMap[doc.Key] = assetId;
                idToDocMap[assetId] = doc.Key;

                var catName = string.IsNullOrWhiteSpace(doc.Category) ? "General" : doc.Category.Trim();
                Category assetCat = categoryLookup[catName];

                var asset = new Asset
                {
                    Id = assetId,
                    AuthorId = authorId,
                    CategoryId = assetCat.Id,
                    Title = doc.Title,
                    Description = doc.Description,
                    Price = 9.99m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SearchRevision = 1L
                };

                foreach (var tagName in doc.Tags)
                {
                    var cleanTag = tagName.Trim();
                    if (tagLookup.TryGetValue(cleanTag, out Tag? tag))
                    {
                        db.AssetTags.Add(new AssetTag
                        {
                            AssetId = assetId,
                            TagId = tag.Id
                        });
                    }
                }

                var version = new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    VersionNumber = 1,
                    IsCurrent = true,
                    StorageKey = $"storage/{doc.Key}/v1.zip",
                    FileName = $"{doc.Key}.zip",
                    ContentLength = 1024,
                    ContentSha256 = new string('0', 64),
                    ReleaseNotes = "Initial",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Personal",
                    LicenseTerms = "Terms",
                    ProcessingStatus = AssetVersionProcessingStatus.READY,
                    ProcessingUpdatedAt = now,
                    CreatedAt = now
                };

                CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                    doc.Title,
                    doc.Description,
                    doc.Category,
                    doc.Tags);

                docStopwatch.Restart();
                var vector = await client.GenerateEmbedding(canonical.CanonicalText, cancellationToken);
                docStopwatch.Stop();
                docLatencies.Add(docStopwatch.Elapsed.TotalMilliseconds);

                var embedding = new AssetEmbedding
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    ModelKey = modelKey,
                    Provider = options.Provider,
                    ModelId = options.Model,
                    ModelRevision = options.Revision,
                    ModelDigest = options.Digest,
                    Dimension = options.Dimension,
                    ContentSchemaVersion = options.ContentSchemaVersion,
                    SourceRevision = 1L,
                    ContentHash = canonical.ContentHash,
                    Embedding = new Vector(vector),
                    CreatedAt = now,
                    UpdatedAt = now
                };

                db.Assets.Add(asset);
                db.AssetVersions.Add(version);
                db.AssetEmbeddings.Add(embedding);
            }

            await db.SaveChangesAsync(cancellationToken);

            (var meanDocLat, _, var p95DocLat, _, _) = CalculateLatencyStats(docLatencies);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] Embedded and seeded {dataset.Documents.Count} documents (Mean latency: {meanDocLat:F1} ms, p95: {p95DocLat:F1} ms).");
            Console.ResetColor();
            Console.WriteLine();
        }

        // 5. Evaluate Lexical and Hybrid Retrieval through production AssetStore
        Console.WriteLine("--> Evaluating production Lexical and Hybrid/RRF retrieval against human judgments...");
        if (qrels.Queries.Count != dataset.Queries.Count)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Qrels queries count ({qrels.Queries.Count}) does not match dataset queries count ({dataset.Queries.Count}). Full one-to-one coverage is required.");
            Console.ResetColor();
            return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
        }

        var queryLookup = dataset.Queries.ToDictionary(q => q.Id, StringComparer.Ordinal);

        var lexicalMetrics = new List<QueryEvaluationMetrics>();
        var hybridMetrics = new List<QueryEvaluationMetrics>();

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var store = new AssetStore(db);

            foreach (HumanQueryQrelsDto adjudicatedQuery in qrels.Queries)
            {
                if (!queryLookup.TryGetValue(adjudicatedQuery.QueryId, out DatasetQueryDto? queryInfo))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ERROR] Query '{adjudicatedQuery.QueryId}' in qrels is unknown in dataset.");
                    Console.ResetColor();
                    return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
                }

                var normalizedQuery = CatalogSearchNormalization.NormalizeSearchQuery(queryInfo.Text) ?? queryInfo.Text;
                var queryVector = await client.GenerateEmbedding(normalizedQuery, cancellationToken);

                var groundTruth = adjudicatedQuery.Judgments.ToDictionary(j => j.DocumentKey, j => j.Relevance);

                // A. Lexical Retrieval
                var lexicalReq = new GetAssetsRequest { Search = normalizedQuery, Page = 1, PageSize = 20 };
                CatalogPageResult<AssetListItem> lexicalResult = await store.GetPaged(lexicalReq, null, null, cancellationToken);
                var lexicalKeys = lexicalResult.Items
                    .Select(item => idToDocMap.GetValueOrDefault(item.Id, string.Empty))
                    .Where(k => !string.IsNullOrEmpty(k))
                    .ToList();

                var lexNdcg = SearchMetrics.CalculateNdcgAt10(lexicalKeys, groundTruth);
                var lexRecall = SearchMetrics.CalculateRecallAt20(lexicalKeys, groundTruth);
                var lexMrr = SearchMetrics.CalculateMrr(lexicalKeys, groundTruth);

                lexicalMetrics.Add(new QueryEvaluationMetrics(
                    queryInfo.Id,
                    queryInfo.Language,
                    queryInfo.Kind,
                    lexNdcg,
                    lexRecall,
                    lexMrr));

                // B. Hybrid / RRF Retrieval
                var hybridReq = new GetAssetsRequest { Search = normalizedQuery, Page = 1, PageSize = 20 };
                CatalogPageResult<AssetListItem> hybridResult = await store.GetPaged(hybridReq, queryVector, modelKey, cancellationToken);
                var hybridKeys = hybridResult.Items
                    .Select(item => idToDocMap.GetValueOrDefault(item.Id, string.Empty))
                    .Where(k => !string.IsNullOrEmpty(k))
                    .ToList();

                var hybNdcg = SearchMetrics.CalculateNdcgAt10(hybridKeys, groundTruth);
                var hybRecall = SearchMetrics.CalculateRecallAt20(hybridKeys, groundTruth);
                var hybMrr = SearchMetrics.CalculateMrr(hybridKeys, groundTruth);

                hybridMetrics.Add(new QueryEvaluationMetrics(
                    queryInfo.Id,
                    queryInfo.Language,
                    queryInfo.Kind,
                    hybNdcg,
                    hybRecall,
                    hybMrr));
            }
        }

        MacroMetricsSummary lexicalSummary = SearchMetrics.CalculateMacroAverage(lexicalMetrics);
        MacroMetricsSummary hybridSummary = SearchMetrics.CalculateMacroAverage(hybridMetrics);

        // 6. Print Comparison and Quality Gates
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Production Retrieval Quality Evaluation (Human Qrels)");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Total Queries:       {hybridSummary.QueryCount}");
        Console.WriteLine($"Lexical Macro nDCG@10: {lexicalSummary.MeanNdcgAt10:F4} | Hybrid: {hybridSummary.MeanNdcgAt10:F4} (Delta: {hybridSummary.MeanNdcgAt10 - lexicalSummary.MeanNdcgAt10:+0.0000;-0.0000;0.0000})");
        Console.WriteLine($"Lexical Macro Recall@20: {lexicalSummary.MeanRecallAt20:F4} | Hybrid: {hybridSummary.MeanRecallAt20:F4} (Delta: {hybridSummary.MeanRecallAt20 - lexicalSummary.MeanRecallAt20:+0.0000;-0.0000;0.0000})");
        Console.WriteLine($"Lexical Macro MRR:     {lexicalSummary.MeanMrr:F4} | Hybrid: {hybridSummary.MeanMrr:F4} (Delta: {hybridSummary.MeanMrr - lexicalSummary.MeanMrr:+0.0000;-0.0000;0.0000})");
        Console.WriteLine("----------------------------------------------------------");

        // Language breakdown
        Console.WriteLine();
        Console.WriteLine("By Language Slice (Hybrid vs Lexical):");
        IOrderedEnumerable<IGrouping<string, QueryEvaluationMetrics>> langGroups = hybridMetrics.GroupBy(m => m.Language).OrderBy(g => g.Key);
        foreach (IGrouping<string, QueryEvaluationMetrics> group in langGroups)
        {
            MacroMetricsSummary hybGroupSummary = SearchMetrics.CalculateMacroAverage(group.ToList());
            var lexGroupMetrics = lexicalMetrics.Where(m => m.Language == group.Key).ToList();
            MacroMetricsSummary lexGroupSummary = SearchMetrics.CalculateMacroAverage(lexGroupMetrics);
            Console.WriteLine($"  [{group.Key,-9}] Count: {hybGroupSummary.QueryCount,-3} | Hybrid nDCG: {hybGroupSummary.MeanNdcgAt10:F4} (Lex: {lexGroupSummary.MeanNdcgAt10:F4}) | Hybrid Recall: {hybGroupSummary.MeanRecallAt20:F4} (Lex: {lexGroupSummary.MeanRecallAt20:F4})");
        }

        // Kind breakdown
        Console.WriteLine();
        Console.WriteLine("By Query Kind (Hybrid vs Lexical):");
        IOrderedEnumerable<IGrouping<string, QueryEvaluationMetrics>> kindGroups = hybridMetrics.GroupBy(m => m.Kind).OrderBy(g => g.Key);
        foreach (IGrouping<string, QueryEvaluationMetrics> group in kindGroups)
        {
            MacroMetricsSummary hybGroupSummary = SearchMetrics.CalculateMacroAverage(group.ToList());
            var lexGroupMetrics = lexicalMetrics.Where(m => m.Kind == group.Key).ToList();
            MacroMetricsSummary lexGroupSummary = SearchMetrics.CalculateMacroAverage(lexGroupMetrics);
            Console.WriteLine($"  [{group.Key,-14}] Count: {hybGroupSummary.QueryCount,-3} | Hybrid nDCG: {hybGroupSummary.MeanNdcgAt10:F4} (Lex: {lexGroupSummary.MeanNdcgAt10:F4}) | Hybrid Recall: {hybGroupSummary.MeanRecallAt20:F4} (Lex: {lexGroupSummary.MeanRecallAt20:F4})");
        }

        // Quality Gate Check (Approved Release Gates)
        QualityEvaluationGateSummary gateSummary = QualityGateEvaluator.Evaluate(lexicalMetrics, hybridMetrics);

        Console.WriteLine();
        Console.WriteLine("Approved Release Quality Gates:");
        foreach (QualityGateResult gate in gateSummary.GateResults)
        {
            var statusStr = gate.Passed ? "PASS" : "FAIL";
            Console.WriteLine($"  - [{statusStr,-4}] {gate.Name,-35} | Requirement: {gate.Requirement,-20} | Lex: {gate.LexicalValue:F4} | Hyb: {gate.HybridValue:F4} (Delta: {gate.Delta:+0.0000;-0.0000;0.0000})");
        }
        Console.WriteLine();

        // Write Quality Evaluation Report (JSON & Markdown)
        var reportData = new QualityEvaluationReportData(
            provenance,
            options,
            qrels.Queries.Count,
            qrels.AdjudicationVersion,
            qrels.AdjudicationDate,
            lexicalSummary,
            hybridSummary,
            gateSummary.GateResults,
            gateSummary.AllGatesPassed);

        (var jsonPath, var mdPath) = SearchEvaluationReportWriter.WriteQualityEvaluationReport(reportData);

        Console.WriteLine("Reports emitted:");
        Console.WriteLine($"  - JSON:     {jsonPath}");
        Console.WriteLine($"  - Markdown: {mdPath}");

        return gateSummary.AllGatesPassed ? Program.EXIT_SUCCESS : Program.EXIT_MANUAL_EVALUATION_REQUIRED;
    }

    public static (double Mean, double P50, double P95, double Min, double Max) CalculateLatencyStats(List<double> latencies)
    {
        if (latencies.Count == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        var sorted = latencies.OrderBy(x => x).ToList();
        var count = sorted.Count;
        var mean = sorted.Average();
        var min = sorted[0];
        var max = sorted[^1];

        // Nearest-rank method for p50 and p95
        var p50Index = (int)Math.Ceiling(0.50 * count) - 1;
        var p95Index = (int)Math.Ceiling(0.95 * count) - 1;

        p50Index = Math.Clamp(p50Index, 0, count - 1);
        p95Index = Math.Clamp(p95Index, 0, count - 1);

        return (mean, sorted[p50Index], sorted[p95Index], min, max);
    }
}
