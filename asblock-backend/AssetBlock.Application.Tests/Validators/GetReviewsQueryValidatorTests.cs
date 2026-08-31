using AssetBlock.Application.UseCases.Reviews.GetReviews;
using AssetBlock.Domain.Core.Dto.Reviews;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class GetReviewsQueryValidatorTests
{
    private readonly GetReviewsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenSortByInvalid_ShouldFail()
    {
        var query = new GetReviewsQuery(Guid.NewGuid(), new GetReviewsRequest { SortBy = "Title" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetReviewsQuery(Guid.NewGuid(), new GetReviewsRequest { SortBy = "Rating" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
