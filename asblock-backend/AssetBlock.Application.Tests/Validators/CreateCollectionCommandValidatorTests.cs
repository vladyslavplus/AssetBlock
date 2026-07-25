using AssetBlock.Application.UseCases.Collections.CreateCollection;
using AssetBlock.Domain.Core.Constants;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class CreateCollectionCommandValidatorTests
{
    private readonly CreateCollectionCommandValidator _validator = new();

    private static CreateCollectionCommand Valid(string title = "My Collection", string? description = "Desc") =>
        new(Guid.NewGuid(), title, description);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_WhenTitleMissing_ShouldFail(string? title)
    {
        var result = await _validator.ValidateAsync(Valid(title!));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateCollectionCommand.Title));
    }

    [Fact]
    public async Task Validate_WhenTitleExceedsMaxLength_ShouldFail()
    {
        var title = new string('a', CollectionConstants.TITLE_MAX_LENGTH + 1);
        var result = await _validator.ValidateAsync(Valid(title));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateCollectionCommand.Title) &&
            e.ErrorMessage.Contains(CollectionConstants.TITLE_MAX_LENGTH.ToString()));
    }

    [Fact]
    public async Task Validate_WhenTitleAtMaxLength_ShouldPass()
    {
        var title = new string('a', CollectionConstants.TITLE_MAX_LENGTH);
        var result = await _validator.ValidateAsync(Valid(title));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenDescriptionExceedsMaxLength_ShouldFail()
    {
        var description = new string('b', CollectionConstants.DESCRIPTION_MAX_LENGTH + 1);
        var result = await _validator.ValidateAsync(Valid(description: description));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateCollectionCommand.Description));
    }

    [Fact]
    public async Task Validate_WhenSellerIdEmpty_ShouldFail()
    {
        var result = await _validator.ValidateAsync(new CreateCollectionCommand(Guid.Empty, "Title", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateCollectionCommand.SellerId));
    }
}
