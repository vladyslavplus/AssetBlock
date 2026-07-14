using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class ElasticsearchOptionsValidatorTests
{
    private readonly ElasticsearchOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenConfigValid_ShouldSucceed()
    {
        var result = _sut.Validate(null, new ElasticsearchOptions
        {
            Url = "http://localhost:9200",
            DefaultIndex = "assets"
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new ElasticsearchOptions
        {
            Url = "",
            DefaultIndex = " "
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Url"));
        result.Failures.Should().Contain(m => m.Contains("DefaultIndex"));
    }

    [Fact]
    public void Validate_WhenUrlNotAbsoluteUri_ShouldFail()
    {
        var result = _sut.Validate(null, new ElasticsearchOptions
        {
            Url = "localhost:9200",
            DefaultIndex = "assets"
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("absolute"));
    }
}
