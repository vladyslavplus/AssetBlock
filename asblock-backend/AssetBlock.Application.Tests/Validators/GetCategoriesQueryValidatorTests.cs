using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Domain.Core.Dto.Categories;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class GetCategoriesQueryValidatorTests
{
    private readonly GetCategoriesQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenSortByInvalid_ShouldFail()
    {
        var query = new GetCategoriesQuery(new GetCategoriesRequest { SortBy = "Nope" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetCategoriesQuery(new GetCategoriesRequest { SortBy = "Name" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
