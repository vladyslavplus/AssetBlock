using AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class UpdateRecommendationPreferencesCommandValidatorTests
{
    private readonly UpdateRecommendationPreferencesCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenUserIdEmpty_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(new UpdateRecommendationPreferencesCommand(Guid.Empty, true));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_WhenValid_ShouldPass(bool personalized)
    {
        ValidationResult result = await _validator.ValidateAsync(new UpdateRecommendationPreferencesCommand(Guid.NewGuid(), personalized));

        result.IsValid.Should().BeTrue();
    }
}
