using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetMyAssetVersionProcessingJobsQueryValidatorTests
{
    private readonly GetMyAssetVersionProcessingJobsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetVersionIdIsEmpty_ShouldFail()
    {
        var query = new GetMyAssetVersionProcessingJobsQuery(Guid.Empty, Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AssetVersionId");
    }

    [Fact]
    public async Task Validate_WhenOwnerUserIdIsEmpty_ShouldFail()
    {
        var query = new GetMyAssetVersionProcessingJobsQuery(Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "OwnerUserId");
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetMyAssetVersionProcessingJobsQuery(Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
