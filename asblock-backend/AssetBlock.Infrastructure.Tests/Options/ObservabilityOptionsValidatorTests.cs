using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public class ObservabilityOptionsValidatorTests
{
    private readonly ObservabilityOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenDisabled_ShouldPassWithDefaultOrEmptyValues()
    {
        var options = new ObservabilityOptions
        {
            Enabled = false,
            ServiceName = string.Empty,
            OtlpEndpoint = "invalid-uri",
            TraceSampleRatio = -1.0
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithValidValues_ShouldPass()
    {
        var options = new ObservabilityOptions
        {
            Enabled = true,
            ServiceName = "MyTestService",
            OtlpEndpoint = "http://127.0.0.1:4317",
            TraceSampleRatio = 1.0
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenEnabledAndServiceNameEmpty_ShouldFail(string? serviceName)
    {
        var options = new ObservabilityOptions
        {
            Enabled = true,
            ServiceName = serviceName!,
            OtlpEndpoint = "http://127.0.0.1:4317",
            TraceSampleRatio = 1.0
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ServiceName is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("/relative/path")]
    public void Validate_WhenEnabledAndEndpointInvalid_ShouldFail(string? endpoint)
    {
        var options = new ObservabilityOptions
        {
            Enabled = true,
            ServiceName = "TestService",
            OtlpEndpoint = endpoint!,
            TraceSampleRatio = 1.0
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("OtlpEndpoint must be a valid absolute HTTP/HTTPS URI");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Validate_WhenEnabledAndTraceSampleRatioOutOfBounds_ShouldFail(double ratio)
    {
        var options = new ObservabilityOptions
        {
            Enabled = true,
            ServiceName = "TestService",
            OtlpEndpoint = "http://127.0.0.1:4317",
            TraceSampleRatio = ratio
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("TraceSampleRatio must be between 0.0 and 1.0");
    }
}
