using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class OllamaTextEmbeddingGeneratorTests
{
    private readonly EmbeddingOptions _defaultOptions = new()
    {
        Enabled = true,
        Provider = "Ollama",
        BaseUrl = "http://127.0.0.1:11434",
        Model = "embeddinggemma:300m-qat-q8_0",
        Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Dimension = 768,
        ContentSchemaVersion = "asset-public-metadata-v1",
        RequestTimeoutSeconds = 5,
        MaxInputChars = 8192
    };

    [Fact]
    public async Task CheckModelAvailability_WhenDisabled_ShouldReturnUnavailableWithoutHttp()
    {
        _defaultOptions.Enabled = false;
        var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        var httpClient = new HttpClient(handler);

        var sut = new OllamaTextEmbeddingGenerator(httpClient, Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        ModelVerificationResult result = await sut.CheckModelAvailability();

        result.IsAvailable.Should().BeFalse();
        result.FailureReason.Should().Contain("disabled");
    }

    [Fact]
    public void Constructor_WhenNonLoopbackUrl_ShouldThrow()
    {
        _defaultOptions.BaseUrl = "http://external-api.com:11434";
        var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        var httpClient = new HttpClient(handler);

        Action act = () => _ = new OllamaTextEmbeddingGenerator(httpClient, Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*loopback*");
    }

    [Fact]
    public async Task CheckModelAvailability_WhenDigestMismatch_ShouldReportUnavailable()
    {
        var tagsResponse = new
        {
            models = new[]
            {
                new
                {
                    name = "embeddinggemma:300m-qat-q8_0",
                    digest = "sha256:differentdigest0000000000000000000000000000000000000000000000000000"
                }
            }
        };

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/api/tags")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(tagsResponse))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        ModelVerificationResult result = await sut.CheckModelAvailability();

        result.IsAvailable.Should().BeFalse();
        result.FailureReason.Should().Contain("digest mismatch");
    }

    [Fact]
    public async Task Generate_WhenVectorDimensionMismatch_ShouldThrow()
    {
        var wrongDimVector = new float[512]; // expected 768
        wrongDimVector[0] = 1.0f;
        var embedResponse = new
        {
            model = "embeddinggemma:300m-qat-q8_0",
            embeddings = new[] { wrongDimVector }
        };

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(embedResponse))
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        Func<Task<GeneratedEmbedding>> act = async () => await sut.Generate("test input");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dimension mismatch*");
    }

    [Fact]
    public async Task Generate_WhenVectorContainsNonFinite_ShouldThrow()
    {
        var rawJson = "{\"model\":\"embeddinggemma:300m-qat-q8_0\",\"embeddings\":[[" + string.Join(",", Enumerable.Repeat("0.0", 767)) + ",1e39]]}";

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawJson)
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        Func<Task<GeneratedEmbedding>> act = async () => await sut.Generate("test input");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-finite*");
    }

    [Fact]
    public async Task Generate_WhenVectorAllZeros_ShouldThrow()
    {
        var zeroVector = new float[768]; // all zeros
        var embedResponse = new
        {
            model = "embeddinggemma:300m-qat-q8_0",
            embeddings = new[] { zeroVector }
        };

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(embedResponse))
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        Func<Task<GeneratedEmbedding>> act = async () => await sut.Generate("test input");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*zero Euclidean norm*");
    }

    [Fact]
    public async Task Generate_WhenValidVector_ShouldReturnGeneratedEmbedding()
    {
        var validVector = new float[768];
        validVector[0] = 0.5f;
        validVector[1] = 0.5f;
        var embedResponse = new
        {
            model = "embeddinggemma:300m-qat-q8_0",
            embeddings = new[] { validVector }
        };

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(embedResponse))
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        GeneratedEmbedding result = await sut.Generate("test input");

        result.Vector.Should().NotBeNull();
        result.Vector.Length.Should().Be(768);
        result.Dimension.Should().Be(768);
        result.ModelKey.Should().Be(EmbeddingModelKey.Compute(_defaultOptions));
    }

    [Fact]
    public async Task CheckModelAvailability_WhenHttpError_DoesNotExposeResponseBody()
    {
        const string sensitiveMarker = "SENSITIVE_PROVIDER_DATA_DO_NOT_LOG_XYZ123";
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"{{\"error\":\"{sensitiveMarker}\"}}")
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        ModelVerificationResult result = await sut.CheckModelAvailability();

        result.IsAvailable.Should().BeFalse();
        result.FailureReason.Should().NotBeNull();
        result.FailureReason.Should().NotContain(sensitiveMarker);
        result.FailureReason.Should().Contain("500");
    }

    [Fact]
    public async Task Generate_WhenHttpError_DoesNotExposeResponseBody()
    {
        const string sensitiveMarker = "SENSITIVE_PROVIDER_DATA_DO_NOT_LOG_XYZ123";
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent($"{{\"error\":\"{sensitiveMarker}\"}}")
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        Func<Task<GeneratedEmbedding>> act = async () => await sut.Generate("test text");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().NotContain(sensitiveMarker)
            .And.Contain("502");
    }

    [Fact]
    public async Task CheckModelAvailability_WhenRepeatedWithinCacheDuration_UsesBoundedCache()
    {
        var httpCallCount = 0;
        var tagsResponse = new
        {
            models = new[]
            {
                new
                {
                    name = "embeddinggemma:300m-qat-q8_0",
                    digest = _defaultOptions.Digest
                }
            }
        };

        var handler = new MockHttpMessageHandler(_ =>
        {
            httpCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tagsResponse))
            };
        });

        var sut = new OllamaTextEmbeddingGenerator(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        ModelVerificationResult first = await sut.CheckModelAvailability();
        ModelVerificationResult second = await sut.CheckModelAvailability();

        first.IsAvailable.Should().BeTrue();
        second.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(1); // Cached!
    }

    [Fact]
    public async Task CheckModelAvailability_WhenCacheExpired_CallsHttpAgain()
    {
        var httpCallCount = 0;
        var tagsResponse = new
        {
            models = new[]
            {
                new
                {
                    name = "embeddinggemma:300m-qat-q8_0",
                    digest = _defaultOptions.Digest
                }
            }
        };

        var handler = new MockHttpMessageHandler(_ =>
        {
            httpCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tagsResponse))
            };
        });

        var fakeTime = new FakeTimeProvider();
        var sut = new OllamaTextEmbeddingGenerator(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            fakeTime);

        ModelVerificationResult first = await sut.CheckModelAvailability();
        first.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(1);

        // Advance within cache window (29 seconds)
        fakeTime.Advance(TimeSpan.FromSeconds(29));
        ModelVerificationResult cached = await sut.CheckModelAvailability();
        cached.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(1);

        // Advance past 30-second cache window
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        ModelVerificationResult expired = await sut.CheckModelAvailability();
        expired.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(2); // Cache expired, HTTP called again!
    }

    [Fact]
    public async Task CheckModelAvailability_WhenUtcClockMovesBackwards_StillExpiresByMonotonicElapsedTime()
    {
        var httpCallCount = 0;
        var tagsResponse = new
        {
            models = new[]
            {
                new
                {
                    name = "embeddinggemma:300m-qat-q8_0",
                    digest = _defaultOptions.Digest
                }
            }
        };

        var handler = new MockHttpMessageHandler(_ =>
        {
            httpCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tagsResponse))
            };
        });

        var fakeTime = new FakeTimeProvider();
        var sut = new OllamaTextEmbeddingGenerator(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            fakeTime);

        ModelVerificationResult first = await sut.CheckModelAvailability();
        first.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(1);

        // Advance monotonic time past 30 seconds, BUT move UTC wall clock backwards by 1 hour (e.g. NTP clock adjustment)
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        fakeTime.MoveClockBackwards(TimeSpan.FromHours(1));

        ModelVerificationResult expired = await sut.CheckModelAvailability();
        expired.IsAvailable.Should().BeTrue();
        httpCallCount.Should().Be(2); // Monotonic elapsed time expired the cache despite backwards wall clock!
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp = 10_000_000;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            _now = _now.Add(amount);
            _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
        }

        public void MoveClockBackwards(TimeSpan amount)
        {
            _now = _now.Subtract(amount);
        }
    }
}
