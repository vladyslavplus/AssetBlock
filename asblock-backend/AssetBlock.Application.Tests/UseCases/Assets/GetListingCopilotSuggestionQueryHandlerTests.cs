using AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetListingCopilotSuggestionQueryHandlerTests
{
    private readonly IListingCopilotStore _store = Substitute.For<IListingCopilotStore>();
    private readonly GetListingCopilotSuggestionQueryHandler _handler;

    public GetListingCopilotSuggestionQueryHandlerTests()
    {
        _handler = new GetListingCopilotSuggestionQueryHandler(_store);
    }

    [Fact]
    public async Task Handle_WhenNotOwned_ShouldReturnNotFound()
    {
        var versionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        _store.GetOwnedVersion(versionId, ownerId, Arg.Any<CancellationToken>())
            .Returns((ListingCopilotOwnedVersion?)null);

        var result = await _handler.Handle(new GetListingCopilotSuggestionQuery(versionId, ownerId), CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
    }

    [Fact]
    public async Task Handle_WhenOwnedWithoutSuggestion_ShouldReturnNotFound()
    {
        var owned = new ListingCopilotOwnedVersion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetVersionProcessingStatus.READY,
            true,
            "a.zip");
        _store.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);
        _store.GetSuggestionForOwner(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ListingCopilotSuggestionDto?)null);

        var result = await _handler.Handle(
            new GetListingCopilotSuggestionQuery(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
    }

    [Fact]
    public async Task Handle_WhenSuggestionExists_ShouldReturnDto()
    {
        var owned = new ListingCopilotOwnedVersion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetVersionProcessingStatus.READY,
            true,
            "a.zip");
        var dto = new ListingCopilotSuggestionDto(
            Guid.NewGuid(),
            owned.AssetVersionId,
            "Chair",
            "A chair",
            "3D",
            ["lowpoly"],
            AiProviderKind.OPENROUTER,
            "fixture/openrouter-test",
            null,
            "TestHost",
            DateTimeOffset.UtcNow);
        _store.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);
        _store.GetSuggestionForOwner(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var result = await _handler.Handle(
            new GetListingCopilotSuggestionQuery(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Value.Should().Be(dto);
        result.Value.Title.Should().Be("Chair");
    }
}
