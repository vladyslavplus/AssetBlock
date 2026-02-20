using AssetBlock.Application.UseCases.Categories.UpdateCategory;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateCategoryCommandValidatorTests
{
    private readonly UpdateCategoryCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenNameIsProvided_AndEmpty_ShouldFail()
    {
        // Sending empty string (not null) is invalid — the name is being "set to nothing"
        var command = new UpdateCategoryCommand(Guid.NewGuid(), "", null, null);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Fact]
    public async Task Validate_WhenNameIsNull_ShouldPass()
    {
        // Null means "don't update" — validator skips it
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, null);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenNameExceeds255Chars_ShouldFail()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), new string('A', 256), null, null);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData("UPPERCASE")] // uppercase isn't allowed
    [InlineData("has space")]  // spaces aren't allowed
    [InlineData("-starts-with-dash")] // leading dash isn't allowed
    [InlineData("ends-with-dash-")]   // trailing dash isn't allowed
    [InlineData("double--dash")]      // consecutive dashes aren't allowed
    public async Task Validate_WhenSlugIsInvalid_ShouldFail(string slug)
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, slug);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Slug");
    }

    [Theory]
    [InlineData("valid-slug")]
    [InlineData("category1")]
    [InlineData("3d-models")]
    [InlineData("a")]
    public async Task Validate_WhenSlugIsValid_ShouldPass(string slug)
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, slug);
        var result = await _validator.ValidateAsync(command);
        result.Errors.Should().NotContain(e => e.PropertyName == "Slug");
    }

    [Fact]
    public async Task Validate_WhenDescriptionExceeds1000Chars_ShouldFail()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, new string('x', 1001), null);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Description");
    }

    [Fact]
    public async Task Validate_WhenAllFieldsAreNull_ShouldPass()
    {
        // Completely empty update (all null) is technically valid from a validator perspective
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, null);
        var result = await _validator.ValidateAsync(command);
        result.IsValid.Should().BeTrue();
    }
}
