using AssetBlock.Application.UseCases.Users.GetMyListings;
using AssetBlock.Domain.Core.Dto.Assets;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetMyListingsQueryValidatorTests
{
    private readonly GetMyListingsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidQuery_ShouldPass()
    {
        var query = new GetMyListingsQuery(Guid.NewGuid(), new GetAssetsRequest { Page = 1, PageSize = 20 });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyAuthorId_ShouldFail()
    {
        var query = new GetMyListingsQuery(Guid.Empty, new GetAssetsRequest { Page = 1, PageSize = 20 });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.AuthorId));
    }

    [Fact]
    public async Task Validate_WhenRequestNull_ShouldFail()
    {
        var query = new GetMyListingsQuery(Guid.NewGuid(), null!);
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenInvalidPage_ShouldFail()
    {
        var query = new GetMyListingsQuery(Guid.NewGuid(), new GetAssetsRequest { Page = 0, PageSize = 20 });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenInvalidSortBy_ShouldFail()
    {
        var query = new GetMyListingsQuery(Guid.NewGuid(), new GetAssetsRequest { Page = 1, PageSize = 20, SortBy = "Unknown" });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }
}
