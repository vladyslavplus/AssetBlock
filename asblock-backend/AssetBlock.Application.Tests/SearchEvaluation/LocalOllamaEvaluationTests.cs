using System.Net;
using System.Text;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation;
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
    public async Task LocalOllamaEvaluator_RunWithMockClient_ShouldProduceValidMetricsAndExitZero()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-and-reviewed",
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
            // Query vector
            ["sword"] = [1.0f, 0.0f],
            // Canonical texts (starts with "title: ...")
            ["title: Sword\ndescription: Steel sword\ncategory: Weapons\ntags: sword"] = [0.95f, 0.05f],
            ["title: Shield\ndescription: Wooden shield\ncategory: Armor\ntags: shield"] = [0.05f, 0.95f]
        });

        var exitCode = await LocalOllamaEvaluator.RunEvaluationAsync(dataset, options, fakeClient);

        exitCode.Should().Be(Program.EXIT_SUCCESS);
    }

    [Fact]
    public void RunDeterministicEvaluation_ProducesIdenticalResultsAcrossRuns()
    {
        var dataset = new DatasetV1Dto(
            1,
            "synthetic-and-reviewed",
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
    public void ResolveEmbeddingOptions_EnforcesPrecedence_AppsettingsUnderEnvironmentUnderCli()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var jsonContent = """
                {
                  "Ai": {
                    "Embeddings": {
                      "Model": "model-appsettings",
                      "Revision": "rev-appsettings",
                      "Digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                      "Dimension": 512
                    }
                  }
                }
                """;
            File.WriteAllText(tempFile, jsonContent);

            // 1. Appsettings baseline
            EmbeddingOptions baseOptions = Program.ResolveEmbeddingOptions(
                tempFile,
                modelOverride: null,
                revisionOverride: null,
                digestOverride: null,
                dimensionOverride: null,
                baseUrlOverride: null,
                timeoutOverride: null);

            baseOptions.Model.Should().Be("model-appsettings");
            baseOptions.Revision.Should().Be("rev-appsettings");
            baseOptions.Dimension.Should().Be(512);

            // 2. Environment variable overrides appsettings
            Environment.SetEnvironmentVariable("Ai__Embeddings__Model", "model-env");
            try
            {
                EmbeddingOptions envOptions = Program.ResolveEmbeddingOptions(
                    tempFile,
                    modelOverride: null,
                    revisionOverride: null,
                    digestOverride: null,
                    dimensionOverride: null,
                    baseUrlOverride: null,
                    timeoutOverride: null);

                envOptions.Model.Should().Be("model-env");
                envOptions.Revision.Should().Be("rev-appsettings");

                // 3. CLI override takes highest precedence over environment variable
                EmbeddingOptions cliOptions = Program.ResolveEmbeddingOptions(
                    tempFile,
                    modelOverride: "model-cli",
                    revisionOverride: null,
                    digestOverride: null,
                    dimensionOverride: null,
                    baseUrlOverride: null,
                    timeoutOverride: null);

                cliOptions.Model.Should().Be("model-cli");
                cliOptions.Revision.Should().Be("rev-appsettings");
            }
            finally
            {
                Environment.SetEnvironmentVariable("Ai__Embeddings__Model", null);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
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
