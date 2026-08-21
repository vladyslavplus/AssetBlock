using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class MinioOptionsValidatorTests
{
    private readonly MinioOptionsValidator _sut = CreateValidator(provider: "Minio");

    [Fact]
    public void Validate_WhenHostPortEndpoint_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "localhost:9000", useSsl: false));
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAbsoluteHttpUri_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:9000", useSsl: false));
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAbsoluteHttpsUri_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "https://minio.example.com", useSsl: true));
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new MinioOptions
        {
            Endpoint = "",
            Bucket = "",
            AccessKey = "",
            SecretKey = ""
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Endpoint"));
        result.Failures.Should().Contain(m => m.Contains("Bucket"));
        result.Failures.Should().Contain(m => m.Contains("AccessKey"));
        result.Failures.Should().Contain(m => m.Contains("SecretKey"));
    }

    [Fact]
    public void Validate_WhenHttpsWithUseSslFalse_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "https://minio.example.com", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("UseSsl"));
    }

    [Fact]
    public void Validate_WhenHttpWithUseSslTrue_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:9000", useSsl: true));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("UseSsl"));
    }

    [Fact]
    public void Validate_WhenUriHasPath_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:9000/minio", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("path"));
    }

    [Fact]
    public void Validate_WhenUriHasQuery_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:9000?x=1", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("query"));
    }

    [Fact]
    public void Validate_WhenHostPortInvalid_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "not a host", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Endpoint"));
    }

    [Fact]
    public void Validate_WhenEndpointPlaceholder_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "<minio-endpoint>:9000", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Endpoint"));
    }

    [Fact]
    public void Validate_WhenHostFormHasPath_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "minio.example.com/path", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("path"));
    }

    [Fact]
    public void Validate_WhenHostFormHasQuery_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "minio.example.com?debug=true", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("query"));
    }

    [Fact]
    public void Validate_WhenHostFormHasFragment_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "minio.example.com#fragment", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("fragment"));
    }

    [Fact]
    public void Validate_WhenEndpointUriInvalidScheme_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "ftp://localhost:9000", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Endpoint"));
    }

    [Fact]
    public void Validate_WhenInactiveProvider_ShouldIgnoreInvalidPlaceholders()
    {
        var sut = CreateValidator(provider: "SeaweedFs");
        var result = sut.Validate(null, new MinioOptions
        {
            Endpoint = "<minio-endpoint>:9000",
            Bucket = "<bucket-name>",
            AccessKey = "<minio-access-key>",
            SecretKey = "<minio-secret-key>",
            UseSsl = true
        });

        result.Succeeded.Should().BeTrue();
    }

    private static MinioOptionsValidator CreateValidator(string provider)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = provider
            })
            .Build();
        return new MinioOptionsValidator(config);
    }

    private static MinioOptions CreateValid(string endpoint, bool useSsl) => new()
    {
        Endpoint = endpoint,
        Bucket = "assets",
        AccessKey = "local-access-key",
        SecretKey = "local-secret-key",
        UseSsl = useSsl
    };
}
