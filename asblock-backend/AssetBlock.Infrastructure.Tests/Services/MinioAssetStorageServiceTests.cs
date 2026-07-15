using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MinioAssetStorageServiceTests
{
    private static MinioAssetStorageService CreateSut(string endpoint)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = endpoint,
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var uri = new Uri(endpoint);
        var client = new MinioClient()
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials("k", "s")
            .WithSSL(false)
            .Build();
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());
        return new MinioAssetStorageService(client, opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
    }

    [Fact]
    public async Task EnsureBucket_swallowsConnectionFailure()
    {
        var sut = CreateSut("http://127.0.0.1:59997");
        await sut.EnsureBucket(CancellationToken.None);
    }

    [Fact]
    public async Task Delete_throwsConnectionFailure_soCallerCanRetry()
    {
        var sut = CreateSut("http://127.0.0.1:59996");
        var act = async () => await sut.Delete("key");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Upload_throws_whenServerUnreachable()
    {
        var sut = CreateSut("http://127.0.0.1:59995");
        var act = async () => await sut.Upload("key", new MemoryStream([1, 2, 3]), 3, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task OpenRead_throws_whenServerUnreachable()
    {
        var sut = CreateSut("http://127.0.0.1:59994");
        var act = async () => await sut.OpenRead("key", (_, _) => Task.CompletedTask);
        await act.Should().ThrowAsync<Exception>();
    }
}
