using AssetBlock.Application.UseCases.Categories.DeleteCategory;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class DeleteCategoryCommandValidatorTests
{
    private readonly DeleteCategoryCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidId_ShouldPass()
    {
        var cmd = new DeleteCategoryCommand(Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyId_ShouldFail()
    {
        var cmd = new DeleteCategoryCommand(Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.Id));
    }
}
