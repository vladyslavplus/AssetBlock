using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class DeleteReviewCommandValidatorTests
{
    private readonly DeleteReviewCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidId_ShouldPass()
    {
        var cmd = new DeleteReviewCommand(Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyId_ShouldFail()
    {
        var cmd = new DeleteReviewCommand(Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.Id));
    }
}
