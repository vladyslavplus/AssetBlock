using System.Net;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Evaluation;
using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Ollama;
using AssetBlock.SearchEvaluation.Validation;
using AssetBlock.SearchEvaluation.VectorOperations;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.SearchEvaluation;

public class LocalOllamaEvaluationTests
{
    private const string VALID_DIGEST = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void VectorMath_CosineSimilarity_ShouldComputeExactValues()
    {
        var a = new[] { 1f, 0f, 0f };
        var b = new[] { 1f, 0f, 0f };
        var c = new[] { 0f, 1f, 0f };
        var d = new[] { -1f, 0f, 0f };

        VectorMath.CosineSimilarity(a, b).Should().BeApproximately(1.0, 0.0001);
        VectorMath.CosineSimilarity(a, c).Should().BeApproximately(0.0, 0.0001);
        VectorMath.CosineSimilarity(a, d).Should().BeApproximately(-1.0, 0.0001);
    }

    [Fact]
    public void VectorMath_ValidateVector_WhenDimensionMismatches_ShouldThrow()
    {
        var vec = new[] { 0.1f, 0.2f, 0.3f };
        Action act = () => VectorMath.ValidateVector(vec, 4);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dimension mismatch*");
    }

    [Fact]
    public void VectorMath_ValidateVector_WhenNonFiniteValues_ShouldThrow()
    {
        var nanVec = new[] { 0.1f, float.NaN, 0.3f };
        var infVec = new[] { 0.1f, float.PositiveInfinity, 0.3f };

        Action actNan = () => VectorMath.ValidateVector(nanVec, 3);
        Action actInf = () => VectorMath.ValidateVector(infVec, 3);

        actNan.Should().Throw<InvalidOperationException>().WithMessage("*non-finite*");
        actInf.Should().Throw<InvalidOperationException>().WithMessage("*non-finite*");
    }

    [Fact]
    public void VectorMath_ValidateVector_WhenZeroNorm_ShouldThrow()
    {
        var zeroVec = new[] { 0f, 0f, 0f };
        Action act = () => VectorMath.ValidateVector(zeroVec, 3);

        act.Should().Throw<InvalidOperationException>().WithMessage("*zero Euclidean norm*");
    }

    [Fact]
    public void LocalOllamaEmbeddingClient_WhenRemoteUrlProvided_ShouldThrowImmediately()
    {
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://remote-machine.example.com:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        using var httpClient = new HttpClient();
        Action act = () =>
        {
            var _ = new LocalOllamaEmbeddingClient(httpClient, options);
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be an absolute loopback HTTP URL*");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_CheckModelAvailability_WhenOffline_ShouldReturnFalseWithBlockerMessage()
    {
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:59999", // Unused port on loopback
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        using var client = new HttpClient();
        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);

        ModelVerificationResult result = await ollamaClient.CheckModelAvailability(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unreachable");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_CheckModelAvailability_WhenModelNotInTags_ShouldReturnFalse()
    {
        var mockHandler = new MockHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"models\":[{\"name\":\"other-model:v1\",\"digest\":\"sha256:1111111111111111111111111111111111111111111111111111111111111111\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var client = new HttpClient(mockHandler);
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        ModelVerificationResult result = await ollamaClient.CheckModelAvailability(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not installed in local Ollama");
        result.ErrorMessage.Should().Contain("bge-m3:q8_0");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_CheckModelAvailability_WhenDigestMismatches_ShouldReturnFalse()
    {
        const string differentDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var mockHandler = new MockHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"models\":[{{\"name\":\"bge-m3:q8_0\",\"digest\":\"{differentDigest}\"}}]}}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var client = new HttpClient(mockHandler);
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        ModelVerificationResult result = await ollamaClient.CheckModelAvailability(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("digest mismatch");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_GenerateEmbedding_WhenDimensionMismatches_ShouldThrow()
    {
        var mockHandler = new MockHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"embeddings\":[[0.1,0.2,0.3]]}", Encoding.UTF8, "application/json")
            });

        using var client = new HttpClient(mockHandler);
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 4 // Configured 4, but Ollama returned 3
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        Func<Task> act = async () => await ollamaClient.GenerateEmbedding("test text", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dimension mismatch*");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_GenerateEmbedding_WhenCancelled_ShouldThrowTaskCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var client = new HttpClient();
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 4
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        Func<Task> act = async () => await ollamaClient.GenerateEmbedding("test text", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void CalculateLatencyStats_WhenLatenciesProvided_ShouldComputeExactPercentiles()
    {
        var latencies = new List<double> { 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0 };
        (double Mean, double P50, double P95, double Min, double Max) stats = LocalOllamaEvaluator.CalculateLatencyStats(latencies);

        stats.Mean.Should().Be(55.0);
        stats.Min.Should().Be(10.0);
        stats.Max.Should().Be(100.0);
        stats.P50.Should().Be(50.0);
        stats.P95.Should().Be(100.0);
    }

    [Fact]
    public async Task LocalOllamaEvaluator_WhenQrelsMissing_ShouldReturnBlockedExitCode()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [
                new DatasetDocumentDto("asset-001", "Sword", "Steel sword", "Weapons", ["sword"]),
                new DatasetDocumentDto("asset-002", "Shield", "Wooden shield", "Armor", ["shield"])
            ],
            [
                new DatasetQueryDto("q-1", "en", "natural", "sword", [new QueryJudgmentDto("asset-001", 3)])
            ]);

        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Revision = "rev-1",
            Digest = VALID_DIGEST,
            Dimension = 2
        };

        var fakeClient = new FakeOllamaEmbeddingClient(new Dictionary<string, float[]>
        {
            ["sword"] = [1.0f, 0.0f]
        });

        var exitCode = await LocalOllamaEvaluator.RunEvaluationAsync(dataset, options, fakeClient);

        exitCode.Should().Be(Program.EXIT_MANUAL_EVALUATION_REQUIRED);
    }

    [Fact]
    public async Task LocalOllamaEvaluator_WhenQrelsProvenanceIsSynthetic_ShouldReturnBlockedExitCode()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var invalidQrels = """
            {
              "schemaVersion": 1,
              "provenance": "synthetic-fixtures",
              "adjudicationVersion": "1.0",
              "adjudicationDate": "2026-09-08",
              "queries": [
                {
                  "queryId": "q-1",
                  "judgments": [
                    { "documentId": "asset-001", "relevance": 3 }
                  ]
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(tempFile, invalidQrels);

            var dataset = new DatasetV1Dto(
                1,
                "synthetic-fixtures",
                [
                    new DatasetDocumentDto("asset-001", "Sword", "Steel sword", "Weapons", ["sword"])
                ],
                [
                    new DatasetQueryDto("q-1", "en", "natural", "sword", [new QueryJudgmentDto("asset-001", 3)])
                ]);

            var options = new EmbeddingOptions
            {
                BaseUrl = "http://127.0.0.1:11434",
                Model = "bge-m3:q8_0",
                Revision = "rev-1",
                Digest = VALID_DIGEST,
                Dimension = 2
            };

            var fakeClient = new FakeOllamaEmbeddingClient([]);

            var exitCode = await LocalOllamaEvaluator.RunEvaluationAsync(dataset, tempFile, options, fakeClient);

            exitCode.Should().Be(Program.EXIT_MANUAL_EVALUATION_REQUIRED);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void RunDeterministicEvaluation_ProducesIdenticalResultsAcrossRuns()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [
                new DatasetDocumentDto("asset-001", "Sword of Light", "Steel blade with radiant glow", "Weapons", ["sword", "light"]),
                new DatasetDocumentDto("asset-002", "Shield of Iron", "Heavy shield forged with dark iron", "Armor", ["shield", "iron"]),
                new DatasetDocumentDto("asset-003", "Sword of Shadow", "Curved sword bathed in dark energy", "Weapons", ["sword", "shadow"]),
                new DatasetDocumentDto("asset-004", "Iron Helm", "Protective helmet of iron", "Armor", ["helm", "iron"])
            ],
            [
                new DatasetQueryDto("q-1", "en", "keyword", "sword iron", [
                    new QueryJudgmentDto("asset-001", 2),
                    new QueryJudgmentDto("asset-002", 2),
                    new QueryJudgmentDto("asset-003", 2),
                    new QueryJudgmentDto("asset-004", 1)
                ])
            ]);

        var exit1 = Program.RunDeterministicEvaluation(dataset, out MacroMetricsSummary summary1);
        var exit2 = Program.RunDeterministicEvaluation(dataset, out MacroMetricsSummary summary2);

        exit1.Should().Be(Program.EXIT_SUCCESS);
        exit2.Should().Be(Program.EXIT_SUCCESS);

        summary1.MeanNdcgAt10.Should().Be(summary2.MeanNdcgAt10);
        summary1.MeanRecallAt20.Should().Be(summary2.MeanRecallAt20);
        summary1.MeanMrr.Should().Be(summary2.MeanMrr);

        // Also, assert reversal of input documents list produces bit-identical metrics due to ordinal tie-breaking
        var reversedDocs = dataset.Documents.AsEnumerable().Reverse().ToList();
        var reversedDataset = new DatasetV1Dto(dataset.Version, dataset.Provenance, reversedDocs, dataset.Queries);

        var exitReversed = Program.RunDeterministicEvaluation(reversedDataset, out MacroMetricsSummary summaryReversed);
        exitReversed.Should().Be(Program.EXIT_SUCCESS);

        summary1.MeanNdcgAt10.Should().Be(summaryReversed.MeanNdcgAt10);
        summary1.MeanRecallAt20.Should().Be(summaryReversed.MeanRecallAt20);
        summary1.MeanMrr.Should().Be(summaryReversed.MeanMrr);
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_CheckModelAvailability_WhenModelHasMissingDigestInTags_ShouldReturnFalse()
    {
        var mockHandler = new MockHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"models\":[{\"name\":\"bge-m3:q8_0\",\"digest\":\"\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var client = new HttpClient(mockHandler);
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        ModelVerificationResult result = await ollamaClient.CheckModelAvailability(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not expose a valid SHA-256 digest");
    }

    [Fact]
    public async Task LocalOllamaEmbeddingClient_CheckModelAvailability_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var client = new HttpClient();
        var options = new EmbeddingOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "bge-m3:q8_0",
            Digest = VALID_DIGEST,
            Dimension = 1024
        };

        var ollamaClient = new LocalOllamaEmbeddingClient(client, options);
        Func<Task> act = async () => await ollamaClient.CheckModelAvailability(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ResolveEmbeddingOptions_LoadsStrictlyFromAppsettings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var jsonContent = """
                {
                  "Ai": {
                    "Embeddings": {
                      "Model": "embeddinggemma:300m-qat-q8_0",
                      "Revision": "pinned-rev",
                      "Digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                      "Dimension": 768,
                      "BaseUrl": "http://127.0.0.1:11434",
                      "RequestTimeoutSeconds": 15
                    }
                  }
                }
                """;
            File.WriteAllText(tempFile, jsonContent);

            EmbeddingOptions options = Program.ResolveEmbeddingOptions(tempFile);

            options.Model.Should().Be("embeddinggemma:300m-qat-q8_0");
            options.Revision.Should().Be("pinned-rev");
            options.Digest.Should().Be("sha256:0000000000000000000000000000000000000000000000000000000000000000");
            options.Dimension.Should().Be(768);
            options.BaseUrl.Should().Be("http://127.0.0.1:11434");
            options.RequestTimeoutSeconds.Should().Be(15);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void QrelsValidator_WhenHumanAdjudicatedAndMatchesDocuments_PassesValidation()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [
                new DatasetDocumentDto("doc-1", "Title", "Desc", "Category", ["tag"])
            ],
            [
                new DatasetQueryDto("q-1", "en", "natural", "test", [new QueryJudgmentDto("doc-1", 3)])
            ]);

        var tempFile = Path.GetTempFileName();
        try
        {
            var validQrels = """
            {
              "version": 1,
              "provenance": "human-adjudicated",
              "adjudicationVersion": "2026-09-08-v1",
              "adjudicationDate": "2026-09-08",
              "queries": [
                {
                  "queryId": "q-1",
                  "judgments": [
                    { "documentKey": "doc-1", "relevance": 3 }
                  ]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, validQrels);

            QrelsValidationResult result = QrelsValidator.ValidateFile(tempFile, dataset);
            result.IsValid.Should().BeTrue();
            result.Qrels.Should().NotBeNull();
            result.Qrels!.Queries.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void QrelsValidator_WhenMissingQueryId_FailsValidation()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [new DatasetDocumentDto("doc-1", "Title", "Desc", "Category", ["tag"])],
            [
                new DatasetQueryDto("q-1", "en", "natural", "query 1", [new QueryJudgmentDto("doc-1", 3)]),
                new DatasetQueryDto("q-2", "en", "natural", "query 2", [new QueryJudgmentDto("doc-1", 3)])
            ]);

        var tempFile = Path.GetTempFileName();
        try
        {
            var qrelsOnlyOneQuery = """
            {
              "version": 1,
              "provenance": "human-adjudicated",
              "adjudicationVersion": "2026-09-08-v1",
              "adjudicationDate": "2026-09-08",
              "queries": [
                {
                  "queryId": "q-1",
                  "judgments": [
                    { "documentKey": "doc-1", "relevance": 3 }
                  ]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, qrelsOnlyOneQuery);

            QrelsValidationResult result = QrelsValidator.ValidateFile(tempFile, dataset);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("missing 1 queries") && e.Contains("q-2"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void QrelsValidator_WhenUnknownQueryId_FailsValidation()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [new DatasetDocumentDto("doc-1", "Title", "Desc", "Category", ["tag"])],
            [new DatasetQueryDto("q-1", "en", "natural", "query 1", [new QueryJudgmentDto("doc-1", 3)])]);

        var tempFile = Path.GetTempFileName();
        try
        {
            var qrelsUnknownQuery = """
            {
              "version": 1,
              "provenance": "human-adjudicated",
              "adjudicationVersion": "2026-09-08-v1",
              "adjudicationDate": "2026-09-08",
              "queries": [
                {
                  "queryId": "q-1",
                  "judgments": [{ "documentKey": "doc-1", "relevance": 3 }]
                },
                {
                  "queryId": "q-unknown",
                  "judgments": [{ "documentKey": "doc-1", "relevance": 3 }]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, qrelsUnknownQuery);

            QrelsValidationResult result = QrelsValidator.ValidateFile(tempFile, dataset);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("unknown queries") && e.Contains("q-unknown"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void QrelsValidator_WhenDuplicateQueryId_FailsValidation()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-fixtures",
            [new DatasetDocumentDto("doc-1", "Title", "Desc", "Category", ["tag"])],
            [new DatasetQueryDto("q-1", "en", "natural", "query 1", [new QueryJudgmentDto("doc-1", 3)])]);

        var tempFile = Path.GetTempFileName();
        try
        {
            var qrelsDuplicateQuery = """
            {
              "version": 1,
              "provenance": "human-adjudicated",
              "adjudicationVersion": "2026-09-08-v1",
              "adjudicationDate": "2026-09-08",
              "queries": [
                {
                  "queryId": "q-1",
                  "judgments": [{ "documentKey": "doc-1", "relevance": 3 }]
                },
                {
                  "queryId": "q-1",
                  "judgments": [{ "documentKey": "doc-1", "relevance": 2 }]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, qrelsDuplicateQuery);

            QrelsValidationResult result = QrelsValidator.ValidateFile(tempFile, dataset);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Duplicate queryId") && e.Contains("q-1"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static (List<QueryEvaluationMetrics> Lex, List<QueryEvaluationMetrics> Hyb) CreatePassingMetricFixtures()
    {
        var lexMetrics = new List<QueryEvaluationMetrics>
        {
            new("q1", "en", "natural", 0.80, 0.88, 0.75),
            new("q2", "uk", "natural", 0.80, 0.88, 0.75),
            new("q3", "technical", "keyword", 0.60, 0.88, 0.75),
            new("q4", "mixed", "cross-language", 0.60, 0.88, 0.75)
        };

        var hybMetrics = new List<QueryEvaluationMetrics>
        {
            new("q1", "en", "natural", 0.80, 0.88, 0.75),
            new("q2", "uk", "natural", 0.80, 0.88, 0.75),
            new("q3", "technical", "keyword", 0.70, 0.88, 0.75), // +0.10 improvement on technical language slice (>= +0.05)
            new("q4", "mixed", "cross-language", 0.70, 0.88, 0.75) // +0.10 improvement on cross-language kind slice (>= +0.05)
        };

        return (lexMetrics, hybMetrics);
    }

    [Fact]
    public void QualityGateEvaluator_WhenAllApprovedGatesPass_ReturnsAllGatesPassedTrue()
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);

        result.AllGatesPassed.Should().BeTrue();
        result.GateResults.Should().AllSatisfy(g => g.Passed.Should().BeTrue());
    }

    [Theory]
    [InlineData("Overall Macro nDCG@10 Absolute", 0.750, 0.88, 0.75, true)]
    [InlineData("Overall Macro nDCG@10 Absolute", 0.749, 0.88, 0.75, false)]
    [InlineData("Overall Macro Recall@20 Absolute", 0.80, 0.850, 0.75, true)]
    [InlineData("Overall Macro Recall@20 Absolute", 0.80, 0.849, 0.75, false)]
    [InlineData("Overall Macro MRR Absolute", 0.80, 0.88, 0.700, true)]
    [InlineData("Overall Macro MRR Absolute", 0.80, 0.88, 0.699, false)]
    public void QualityGateEvaluator_AbsoluteOverallGates_BoundaryChecks(
        string gateName,
        double hybNdcg,
        double hybRecall,
        double hybMrr,
        bool expectedPass)
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();
        for (var i = 0; i < hybMetrics.Count; i++)
        {
            QueryEvaluationMetrics old = hybMetrics[i];
            hybMetrics[i] = new QueryEvaluationMetrics(old.QueryId, old.Language, old.Kind, hybNdcg, hybRecall, hybMrr);
        }

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        QualityGateResult targetGate = result.GateResults.First(g => g.Name == gateName);
        targetGate.Passed.Should().Be(expectedPass);
    }

    [Theory]
    [InlineData("English Slice Absolute nDCG@10", "en", 0.650, 0.88, 0.75, true)]
    [InlineData("English Slice Absolute nDCG@10", "en", 0.649, 0.88, 0.75, false)]
    [InlineData("English Slice Absolute Recall@20", "en", 0.80, 0.750, 0.75, true)]
    [InlineData("English Slice Absolute Recall@20", "en", 0.80, 0.749, 0.75, false)]
    [InlineData("English Slice Absolute MRR", "en", 0.80, 0.88, 0.600, true)]
    [InlineData("English Slice Absolute MRR", "en", 0.80, 0.88, 0.599, false)]
    [InlineData("Technical Slice Absolute nDCG@10", "technical", 0.650, 0.88, 0.75, true)]
    [InlineData("Technical Slice Absolute nDCG@10", "technical", 0.649, 0.88, 0.75, false)]
    [InlineData("Mixed Slice Absolute Recall@20", "mixed", 0.80, 0.750, 0.75, true)]
    [InlineData("Mixed Slice Absolute Recall@20", "mixed", 0.80, 0.749, 0.75, false)]
    [InlineData("Ukrainian Slice Absolute MRR", "uk", 0.80, 0.88, 0.600, true)]
    [InlineData("Ukrainian Slice Absolute MRR", "uk", 0.80, 0.88, 0.599, false)]
    public void QualityGateEvaluator_AbsoluteSliceGates_BoundaryChecks(
        string gateName,
        string lang,
        double hybNdcg,
        double hybRecall,
        double hybMrr,
        bool expectedPass)
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();
        var idx = hybMetrics.FindIndex(m => m.Language == lang);
        QueryEvaluationMetrics old = hybMetrics[idx];
        hybMetrics[idx] = new QueryEvaluationMetrics(old.QueryId, old.Language, old.Kind, hybNdcg, hybRecall, hybMrr);

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        QualityGateResult targetGate = result.GateResults.First(g => g.Name == gateName);
        targetGate.Passed.Should().Be(expectedPass);
    }

    [Theory]
    [InlineData("Overall Macro nDCG@10 Regression", 0.80, 0.780, 0.88, 0.88, 0.75, 0.75, true)] // -0.020 regression exactly
    [InlineData("Overall Macro nDCG@10 Regression", 0.80, 0.779, 0.88, 0.88, 0.75, 0.75, false)] // -0.021 regression
    [InlineData("Overall Macro Recall@20 Comparative", 0.80, 0.80, 0.88, 0.880, 0.75, 0.75, true)] // 0 regression
    [InlineData("Overall Macro Recall@20 Comparative", 0.80, 0.80, 0.88, 0.879, 0.75, 0.75, false)] // negative regression
    [InlineData("Overall Macro MRR Comparative", 0.80, 0.80, 0.88, 0.88, 0.75, 0.750, true)] // 0 regression
    [InlineData("Overall Macro MRR Comparative", 0.80, 0.80, 0.88, 0.88, 0.75, 0.749, false)] // negative regression
    public void QualityGateEvaluator_ComparativeOverallRegression_BoundaryChecks(
        string gateName,
        double lexNdcg, double hybNdcg,
        double lexRecall, double hybRecall,
        double lexMrr, double hybMrr,
        bool expectedPass)
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();
        for (var i = 0; i < lexMetrics.Count; i++)
        {
            QueryEvaluationMetrics l = lexMetrics[i];
            lexMetrics[i] = new QueryEvaluationMetrics(l.QueryId, l.Language, l.Kind, lexNdcg, lexRecall, lexMrr);
            QueryEvaluationMetrics h = hybMetrics[i];
            hybMetrics[i] = new QueryEvaluationMetrics(h.QueryId, h.Language, h.Kind, hybNdcg, hybRecall, hybMrr);
        }

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        QualityGateResult targetGate = result.GateResults.First(g => g.Name == gateName);
        targetGate.Passed.Should().Be(expectedPass);
    }

    [Theory]
    [InlineData("English Slice nDCG@10 Regression", "en", 0.80, 0.780, true)] // -0.020 regression
    [InlineData("English Slice nDCG@10 Regression", "en", 0.80, 0.779, false)] // -0.021 regression
    [InlineData("Ukrainian Slice nDCG@10 Regression", "uk", 0.80, 0.780, true)] // -0.020 regression
    [InlineData("Ukrainian Slice nDCG@10 Regression", "uk", 0.80, 0.779, false)] // -0.021 regression
    public void QualityGateEvaluator_ComparativeSliceRegression_BoundaryChecks(
        string gateName,
        string lang,
        double lexNdcg,
        double hybNdcg,
        bool expectedPass)
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();
        var lIdx = lexMetrics.FindIndex(m => m.Language == lang);
        QueryEvaluationMetrics oldL = lexMetrics[lIdx];
        lexMetrics[lIdx] = new QueryEvaluationMetrics(oldL.QueryId, oldL.Language, oldL.Kind, lexNdcg, oldL.RecallAt20, oldL.Mrr);

        var hIdx = hybMetrics.FindIndex(m => m.Language == lang);
        QueryEvaluationMetrics oldH = hybMetrics[hIdx];
        hybMetrics[hIdx] = new QueryEvaluationMetrics(oldH.QueryId, oldH.Language, oldH.Kind, hybNdcg, oldH.RecallAt20, oldH.Mrr);

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        QualityGateResult targetGate = result.GateResults.First(g => g.Name == gateName);
        targetGate.Passed.Should().Be(expectedPass);
    }

    [Theory]
    [InlineData("Cross-Language Kind Slice Improvement", "mixed", "cross-language", 0.60, 0.650, true)] // +0.050 exactly
    [InlineData("Cross-Language Kind Slice Improvement", "mixed", "cross-language", 0.60, 0.649, false)] // +0.049
    [InlineData("Technical Language Slice Improvement", "technical", "keyword", 0.60, 0.650, true)] // +0.050 exactly
    [InlineData("Technical Language Slice Improvement", "technical", "keyword", 0.60, 0.649, false)] // +0.049
    public void QualityGateEvaluator_ImprovementGates_BoundaryChecks(
        string gateName,
        string lang,
        string kind,
        double lexNdcg,
        double hybNdcg,
        bool expectedPass)
    {
        (List<QueryEvaluationMetrics> lexMetrics, List<QueryEvaluationMetrics> hybMetrics) = CreatePassingMetricFixtures();
        var lIdx = lexMetrics.FindIndex(m => m.Language == lang && m.Kind == kind);
        QueryEvaluationMetrics oldL = lexMetrics[lIdx];
        lexMetrics[lIdx] = new QueryEvaluationMetrics(oldL.QueryId, oldL.Language, oldL.Kind, lexNdcg, oldL.RecallAt20, oldL.Mrr);

        var hIdx = hybMetrics.FindIndex(m => m.Language == lang && m.Kind == kind);
        QueryEvaluationMetrics oldH = hybMetrics[hIdx];
        hybMetrics[hIdx] = new QueryEvaluationMetrics(oldH.QueryId, oldH.Language, oldH.Kind, hybNdcg, oldH.RecallAt20, oldH.Mrr);

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        QualityGateResult targetGate = result.GateResults.First(g => g.Name == gateName);
        targetGate.Passed.Should().Be(expectedPass);
    }

    [Fact]
    public void QualityGateEvaluator_TrackedDataset_HasValidSlicesAndEvaluatesWithoutMissingSlices()
    {
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));
        if (!File.Exists(datasetPath))
        {
            var dir = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "asblock-backend", "search-evaluation", "dataset.v1.json");
                if (File.Exists(candidate))
                {
                    datasetPath = candidate;
                    break;
                }
                DirectoryInfo? parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }
                dir = parent.FullName;
            }
        }

        File.Exists(datasetPath).Should().BeTrue("dataset.v1.json must exist in the repository");

        using var doc = JsonDocument.Parse(File.ReadAllText(datasetPath));
        JsonElement queries = doc.RootElement.GetProperty("queries");
        queries.GetArrayLength().Should().Be(92);

        var queryList = new List<(string Id, string Language, string Kind)>();
        foreach (JsonElement q in queries.EnumerateArray())
        {
            queryList.Add((
                q.GetProperty("id").GetString()!,
                q.GetProperty("language").GetString()!,
                q.GetProperty("kind").GetString()!));
        }

        queryList.Count(q => q.Language == "en").Should().Be(31);
        queryList.Count(q => q.Language == "uk").Should().Be(31);
        queryList.Count(q => q.Language == "technical").Should().Be(23);
        queryList.Count(q => q.Language == "mixed").Should().Be(7);
        queryList.Count(q => q.Kind == "cross-language").Should().Be(17);

        // Build valid metrics for all 92 queries
        var lexMetrics = new List<QueryEvaluationMetrics>(92);
        var hybMetrics = new List<QueryEvaluationMetrics>(92);
        foreach ((var id, var language, var kind) in queryList)
        {
            var isTech = language == "technical";
            var isCross = kind == "cross-language";

            var lexNdcg = (isTech || isCross) ? 0.65 : 0.80;
            var hybNdcg = (isTech || isCross) ? 0.72 : 0.80; // >= +0.05 improvement
            lexMetrics.Add(new QueryEvaluationMetrics(id, language, kind, lexNdcg, 0.88, 0.75));
            hybMetrics.Add(new QueryEvaluationMetrics(id, language, kind, hybNdcg, 0.88, 0.75));
        }

        QualityEvaluationGateSummary result = QualityGateEvaluator.Evaluate(lexMetrics, hybMetrics);
        result.AllGatesPassed.Should().BeTrue();
        result.GateResults.Should().AllSatisfy(g => g.Passed.Should().BeTrue());
    }

    [Fact]
    public void SearchBenchmarkRunner_IsPrescribedDecisionProtocol_ValidatesExactCriteria()
    {
        SearchBenchmarkRunner.IsPrescribedDecisionProtocol([1000, 10000, 50000], 100, 1000, [1, 10])
            .Should().BeTrue();

        // Warmup differs
        SearchBenchmarkRunner.IsPrescribedDecisionProtocol([1000, 10000, 50000], 10, 1000, [1, 10])
            .Should().BeFalse();

        // Samples differ
        SearchBenchmarkRunner.IsPrescribedDecisionProtocol([1000, 10000, 50000], 100, 50, [1, 10])
            .Should().BeFalse();

        // Concurrency differs
        SearchBenchmarkRunner.IsPrescribedDecisionProtocol([1000, 10000, 50000], 100, 1000, [1])
            .Should().BeFalse();

        // Sizes differ
        SearchBenchmarkRunner.IsPrescribedDecisionProtocol([1000, 10000], 100, 1000, [1, 10])
            .Should().BeFalse();
    }

    private sealed class FakeOllamaEmbeddingClient(Dictionary<string, float[]> mapping) : IOllamaEmbeddingClient
    {
        public Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelVerificationResult(true, null, VALID_DIGEST));
        }

        public Task<float[]> GenerateEmbedding(string text, CancellationToken cancellationToken)
        {
            if (mapping.TryGetValue(text, out var vec))
            {
                return Task.FromResult(vec);
            }

            // Fallback synthetic vector
            return Task.FromResult(new[] { 0.5f, 0.5f });
        }
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            return Task.FromResult(handler(request));
        }
    }
}
