using AssetBlock.Application.UseCases.Reviews.CreateReview;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class CreateReviewCommandValidatorTests
{
    private readonly CreateReviewCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenRatingOutOfRange_ShouldFail()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 6, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenCommentTooLong_ShouldFail()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, new string('x', 1001));
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 3, "ok");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
