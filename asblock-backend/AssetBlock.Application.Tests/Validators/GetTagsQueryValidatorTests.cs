using AssetBlock.Application.UseCases.Tags.GetTags;
using AssetBlock.Domain.Core.Dto.Tags;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetTagsQueryValidatorTests
{
    private readonly GetTagsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidQuery_ShouldPass()
    {
        var query = new GetTagsQuery(new GetTagsRequest { Page = 1, PageSize = 20 });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenRequestNull_ShouldFail()
    {
        var query = new GetTagsQuery(null!);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenPageSizeZero_ShouldFail()
    {
        var query = new GetTagsQuery(new GetTagsRequest { Page = 1, PageSize = 0 });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenInvalidSortBy_ShouldFail()
    {
        var query = new GetTagsQuery(new GetTagsRequest { Page = 1, PageSize = 20, SortBy = "invalid_sort" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }
}
