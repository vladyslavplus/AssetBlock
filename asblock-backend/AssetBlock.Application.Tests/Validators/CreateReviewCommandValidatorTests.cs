using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class CreateReviewCommandValidatorTests
{
    private readonly CreateReviewCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenRatingOutOfRange_ShouldFail()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 6, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenCommentTooLong_ShouldFail()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, new string('x', 1001));
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 3, "ok");
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
