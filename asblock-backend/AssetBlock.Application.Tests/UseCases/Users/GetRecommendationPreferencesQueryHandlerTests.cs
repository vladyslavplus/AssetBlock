using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.GetRecommendationPreferences;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class GetRecommendationPreferencesQueryHandlerTests
{
    private readonly IRecommendationPersonalizationStore _personalizationStore = Substitute.For<IRecommendationPersonalizationStore>();

    [Fact]
    public async Task Handle_WhenPreferenceMissing_ShouldReturnDefaultOff()
    {
        var id = Guid.NewGuid();
        _personalizationStore.GetPreferences(id, Arg.Any<CancellationToken>())
            .Returns((UserRecommendationPreferences?)null);
        var handler = new GetRecommendationPreferencesQueryHandler(_personalizationStore);

        Result<RecommendationPreferencesDto> result = await handler.Handle(new GetRecommendationPreferencesQuery(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPersonalized.Should().BeFalse();
        result.Value.OptedInAt.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenOptedIn_ShouldReturnSavedState()
    {
        var id = Guid.NewGuid();
        DateTimeOffset optedInAt = DateTimeOffset.UtcNow.AddDays(-1);
        _personalizationStore.GetPreferences(id, Arg.Any<CancellationToken>())
            .Returns(new UserRecommendationPreferences { UserId = id, IsPersonalized = true, OptedInAt = optedInAt, UpdatedAt = optedInAt });
        var handler = new GetRecommendationPreferencesQueryHandler(_personalizationStore);

        Result<RecommendationPreferencesDto> result = await handler.Handle(new GetRecommendationPreferencesQuery(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPersonalized.Should().BeTrue();
        result.Value.OptedInAt.Should().Be(optedInAt);
    }
}
