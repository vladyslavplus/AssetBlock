using AssetBlock.WebApi.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class FileUploadExtensionsTests
{
    [Fact]
    public void AddFileUploadLimits_ShouldAllowConfiguredFileSizePlusMultipartOverhead()
    {
        const long maxFileBytes = 100;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileUpload:MaxFileBytes"] = maxFileBytes.ToString()
            })
            .Build();
        var services = new ServiceCollection();

        services.AddFileUploadLimits(configuration);
        using var provider = services.BuildServiceProvider();

        var expectedRequestBytes = maxFileBytes + FileUploadExtensions.MULTIPART_OVERHEAD_BYTES;
        provider.GetRequiredService<IOptions<FormOptions>>().Value.MultipartBodyLengthLimit
            .Should().Be(expectedRequestBytes);
        provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize
            .Should().Be(expectedRequestBytes);
    }
}
