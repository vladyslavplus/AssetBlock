using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MinioAssetStorageServiceTests
{
    private static S3CompatibleAssetStorageService CreateSut(string endpoint)
    {
        var uri = new Uri(endpoint);
        IMinioClient client = new MinioClient()
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials("k", "s")
            .WithSSL(false)
            .Build();
        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());
        return new S3CompatibleAssetStorageService(client, "b", resilience, NullLogger<S3CompatibleAssetStorageService>.Instance);
    }

    [Fact]
    public async Task EnsureBucket_WhenServerUnreachable_ShouldPropagate()
    {
        S3CompatibleAssetStorageService sut = CreateSut("http://127.0.0.1:59997");
        Func<Task> act = async () => await sut.EnsureBucket(CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Delete_throwsConnectionFailure_soCallerCanRetry()
    {
        S3CompatibleAssetStorageService sut = CreateSut("http://127.0.0.1:59996");
        Func<Task> act = async () => await sut.Delete("key");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Upload_throws_whenServerUnreachable()
    {
        S3CompatibleAssetStorageService sut = CreateSut("http://127.0.0.1:59995");
        Func<Task> act = async () => await sut.Upload("key", new MemoryStream([1, 2, 3]), 3, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task OpenRead_throws_whenServerUnreachable()
    {
        S3CompatibleAssetStorageService sut = CreateSut("http://127.0.0.1:59994");
        Func<Task> act = async () => await sut.OpenRead("key", (_, _) => Task.CompletedTask);
        await act.Should().ThrowAsync<Exception>();
    }
}
