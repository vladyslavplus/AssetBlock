using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class SeaweedFsOptionsValidatorTests
{
    private readonly SeaweedFsOptionsValidator _sut = CreateValidator(provider: "SeaweedFs");

    [Fact]
    public void Validate_WhenAbsoluteHttpUri_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, CreateValid(endpoint: "http://localhost:8333", useSsl: false));
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new SeaweedFsOptions
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
        SeaweedFsOptionsValidator sut = CreateValidator(provider: "Minio");
        ValidateOptionsResult result = sut.Validate(null, new SeaweedFsOptions
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
        ValidateOptionsResult result = _sut.Validate(null, CreateValid(endpoint: "https://seaweed.example.com", useSsl: false));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("SeaweedFs:UseSsl"));
    }

    private static SeaweedFsOptionsValidator CreateValidator(string provider)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
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
