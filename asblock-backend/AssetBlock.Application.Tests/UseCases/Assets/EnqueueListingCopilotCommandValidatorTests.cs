using AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;
using AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class EnqueueListingCopilotCommandValidatorTests
{
    private readonly EnqueueListingCopilotCommandValidator _enqueue = new();
    private readonly GetListingCopilotSuggestionQueryValidator _get = new();

    [Fact]
    public void Validate_WhenIdsMissing_ShouldFail()
    {
        _enqueue.Validate(new EnqueueListingCopilotCommand(Guid.Empty, Guid.Empty)).IsValid.Should().BeFalse();
        _get.Validate(new GetListingCopilotSuggestionQuery(Guid.Empty, Guid.Empty)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenIdsPresent_ShouldPass()
    {
        var versionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        _enqueue.Validate(new EnqueueListingCopilotCommand(versionId, ownerId)).IsValid.Should().BeTrue();
        _get.Validate(new GetListingCopilotSuggestionQuery(versionId, ownerId)).IsValid.Should().BeTrue();
    }
}
