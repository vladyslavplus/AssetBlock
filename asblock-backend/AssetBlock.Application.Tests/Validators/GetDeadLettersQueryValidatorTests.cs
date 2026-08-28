using AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;
using AssetBlock.Domain.Core.Dto.Outbox;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetDeadLettersQueryValidatorTests
{
    private readonly GetDeadLettersQueryValidator _validator = new();

    [Fact]
    public void Validate_WhenRequestValid_ShouldNotHaveErrors()
    {
        var query = new GetDeadLettersQuery(new GetDeadLettersRequest());
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPageInvalid_ShouldHaveError(int page)
    {
        var query = new GetDeadLettersQuery(new GetDeadLettersRequest(page));
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Page"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void Validate_WhenPageSizeInvalid_ShouldHaveError(int pageSize)
    {
        var query = new GetDeadLettersQuery(new GetDeadLettersRequest(1, pageSize));
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("PageSize"));
    }
}
