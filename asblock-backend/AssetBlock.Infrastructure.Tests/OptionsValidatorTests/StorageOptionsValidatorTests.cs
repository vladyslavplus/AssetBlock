using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class StorageOptionsValidatorTests
{
    private readonly StorageOptionsValidator _sut = new();

    [Theory]
    [InlineData("SeaweedFs")]
    [InlineData("seaweedfs")]
    [InlineData("Minio")]
    [InlineData("MINIO")]
    public void Validate_WhenKnownProvider_ShouldSucceed(string provider)
    {
        ValidateOptionsResult result = _sut.Validate(null, new StorageOptions { Provider = provider });
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenMissingProvider_ShouldFail(string? provider)
    {
        ValidateOptionsResult result = _sut.Validate(null, new StorageOptions { Provider = provider! });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Storage:Provider"));
    }

    [Fact]
    public void Validate_WhenUnknownProvider_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new StorageOptions { Provider = "AzureBlob" });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("unknown"));
    }
}
