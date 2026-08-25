using AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetMyAssetProcessingJobsQueryValidatorTests
{
    private readonly GetMyAssetProcessingJobsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdIsEmpty_ShouldFail()
    {
        var query = new GetMyAssetProcessingJobsQuery(Guid.Empty, Guid.NewGuid());
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AssetId");
    }

    [Fact]
    public async Task Validate_WhenOwnerUserIdIsEmpty_ShouldFail()
    {
        var query = new GetMyAssetProcessingJobsQuery(Guid.NewGuid(), Guid.Empty);
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "OwnerUserId");
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetMyAssetProcessingJobsQuery(Guid.NewGuid(), Guid.NewGuid());
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
