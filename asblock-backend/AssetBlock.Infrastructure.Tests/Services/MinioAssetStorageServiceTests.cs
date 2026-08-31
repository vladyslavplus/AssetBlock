using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MinioAssetStorageServiceTests
{
    private static MinioAssetStorageService CreateSut(string endpoint)
    {
        IOptions<MinioOptions> opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = endpoint,
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var uri = new Uri(endpoint);
        IMinioClient client = new MinioClient()
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials("k", "s")
            .WithSSL(false)
            .Build();
        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());
        return new MinioAssetStorageService(client, opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
    }

    [Fact]
    public async Task EnsureBucket_WhenServerUnreachable_ShouldPropagate()
    {
        MinioAssetStorageService sut = CreateSut("http://127.0.0.1:59997");
        Func<Task> act = async () => await sut.EnsureBucket(CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Delete_throwsConnectionFailure_soCallerCanRetry()
    {
        MinioAssetStorageService sut = CreateSut("http://127.0.0.1:59996");
        Func<Task> act = async () => await sut.Delete("key");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Upload_throws_whenServerUnreachable()
    {
        MinioAssetStorageService sut = CreateSut("http://127.0.0.1:59995");
        Func<Task> act = async () => await sut.Upload("key", new MemoryStream([1, 2, 3]), 3, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task OpenRead_throws_whenServerUnreachable()
    {
        MinioAssetStorageService sut = CreateSut("http://127.0.0.1:59994");
        Func<Task> act = async () => await sut.OpenRead("key", (_, _) => Task.CompletedTask);
        await act.Should().ThrowAsync<Exception>();
    }
}
