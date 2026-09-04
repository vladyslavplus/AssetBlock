using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class S3CompatibleObjectStoreContentTypeTests
{
    [Fact]
    public async Task Upload_SetsBinaryOctetStreamContentType()
    {
        IMinioClient client = Substitute.For<IMinioClient>();
        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>()).Returns(_ => new ResiliencePipelineBuilder().Build());

        var store = new S3CompatibleObjectStore(client, "test-bucket", resilience, NullLogger.Instance);

        PutObjectArgs? capturedArgs = null;
        client.PutObjectAsync(Arg.Do<PutObjectArgs>(args => capturedArgs = args), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Minio.DataModel.Response.PutObjectResponse(
                System.Net.HttpStatusCode.OK,
                "test-bucket",
                new Dictionary<string, string>(),
                0,
                "etag")));

        using var stream = new MemoryStream([1, 2, 3, 4]);
        await store.Upload("test-key", stream, 4, CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        System.Reflection.PropertyInfo? contentTypeProp = typeof(PutObjectArgs).GetProperty(
            "ContentType",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualContentType = contentTypeProp?.GetValue(capturedArgs) as string;
        actualContentType.Should().Be("application/octet-stream");
    }
}
