using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<secret>")]
    [InlineData(" <jwt-key> ")]
    [InlineData("<minio-endpoint>:9000")]
    public void IsMissingOrPlaceholder_WhenEmptyOrPlaceholder_ShouldBeTrue(string? value)
    {
        OptionsValidation.IsMissingOrPlaceholder(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost:9000")]
    [InlineData("real-secret-value")]
    [InlineData("http://localhost:9000")]
    public void IsMissingOrPlaceholder_WhenRealValue_ShouldBeFalse(string value)
    {
        OptionsValidation.IsMissingOrPlaceholder(value).Should().BeFalse();
    }
}
