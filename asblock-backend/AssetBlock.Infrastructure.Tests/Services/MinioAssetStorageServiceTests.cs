using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MinioAssetStorageServiceTests
{
    [Fact]
    public async Task EnsureBucket_swallowsConnectionFailure()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = "127.0.0.1:59997",
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new MinioAssetStorageService(opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
        await sut.EnsureBucket(CancellationToken.None);
    }

    [Fact]
    public async Task Delete_swallowsConnectionFailure()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = "127.0.0.1:59996",
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new MinioAssetStorageService(opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
        await sut.Delete("key");
    }

    [Fact]
    public async Task Upload_throws_whenServerUnreachable()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = "127.0.0.1:59995",
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new MinioAssetStorageService(opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
        var act = async () => await sut.Upload("key", new MemoryStream([1, 2, 3]), CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Get_throws_whenServerUnreachable()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = "127.0.0.1:59994",
            Bucket = "b",
            AccessKey = "k",
            SecretKey = "s",
            UseSsl = false
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new MinioAssetStorageService(opts, resilience, NullLogger<MinioAssetStorageService>.Instance);
        var act = async () => await sut.Get("key");
        await act.Should().ThrowAsync<Exception>();
    }
}
