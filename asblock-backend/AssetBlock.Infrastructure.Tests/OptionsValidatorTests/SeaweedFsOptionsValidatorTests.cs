using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class SeaweedFsOptionsValidatorTests
{
    private readonly SeaweedFsOptionsValidator _sut = CreateValidator(provider: "SeaweedFs");

    [Fact]
    public void Validate_WhenAbsoluteHttpUri_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:8333", useSsl: false));
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new SeaweedFsOptions
        {
            Endpoint = "",
            Bucket = "",
            AccessKey = "",
            SecretKey = ""
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("SeaweedFs:Endpoint"));
        result.Failures.Should().Contain(m => m.Contains("SeaweedFs:Bucket"));
    }

    [Fact]
    public void Validate_WhenInactiveProvider_ShouldIgnoreInvalidPlaceholders()
    {
        var sut = CreateValidator(provider: "Minio");
        var result = sut.Validate(null, new SeaweedFsOptions
        {
            Endpoint = "<seaweedfs-endpoint>:8333",
            Bucket = "<bucket-name>",
            AccessKey = "<seaweedfs-access-key>",
            SecretKey = "<seaweedfs-secret-key>",
            UseSsl = true
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenHttpsWithUseSslFalse_ShouldFail()
    {
        var result = _sut.Validate(null, CreateValid(endpoint: "https://seaweed.example.com", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("SeaweedFs:UseSsl"));
    }

    private static SeaweedFsOptionsValidator CreateValidator(string provider)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = provider
            })
            .Build();
        return new SeaweedFsOptionsValidator(config);
    }

    private static SeaweedFsOptions CreateValid(string endpoint, bool useSsl) => new()
    {
        Endpoint = endpoint,
        Bucket = "assets",
        AccessKey = "local-access-key",
        SecretKey = "local-secret-key",
        UseSsl = useSsl
    };
}
