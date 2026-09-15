using Ardalis.Result;
using AssetBlock.Application.UseCases.Recommendations.IngestRecommendationEvent;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Recommendations;

public sealed class IngestRecommendationEventCommandHandlerTests
{
    private readonly IRecommendationExposureSigner _signer = Substitute.For<IRecommendationExposureSigner>();
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IRecommendationEventStore _store = Substitute.For<IRecommendationEventStore>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly IngestRecommendationEventCommandHandler _handler;
    private static readonly DateTimeOffset _now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    public IngestRecommendationEventCommandHandlerTests()
    {
        _timeProvider.GetUtcNow().Returns(_now);
        _signer.TryVerify(Arg.Any<RecommendationExposurePayload>(), Arg.Any<string>()).Returns(true);
        _store.TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        _handler = new IngestRecommendationEventCommandHandler(
            _signer,
            _assetStore,
            _store,
            NullLogger<IngestRecommendationEventCommandHandler>.Instance,
            _timeProvider);
    }

    [Fact]
    public async Task Handle_WhenEnvelopeIsValidAndTargetsPublic_ShouldInsert()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        Result result = await _handler.Handle(Command(sourceId, targetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        RecommendationEvent inserted = CapturedEvent();
        inserted.SourceAssetId.Should().Be(sourceId);
        inserted.TargetAssetId.Should().Be(targetId);
        inserted.OccurredAt.Should().Be(_now);
        inserted.ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenEventIdReplayed_ShouldSucceed()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _store.TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>()).Returns(false);

        Result result = await _handler.Handle(Command(sourceId, targetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTokenIsForged_ShouldSucceedWithoutInsert()
    {
        _signer.TryVerify(Arg.Any<RecommendationExposurePayload>(), Arg.Any<string>()).Returns(false);

        await ShouldSucceedWithoutInsert(Command(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Handle_WhenExposureExpired_ShouldSucceedWithoutInsert()
    {
        IngestRecommendationEventCommand command = Command(Guid.NewGuid(), Guid.NewGuid(), expiresAt: _now.AddSeconds(-1));

        await ShouldSucceedWithoutInsert(command);
        _signer.DidNotReceive().TryVerify(Arg.Any<RecommendationExposurePayload>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenSlotDoesNotMatchTarget_ShouldSucceedWithoutInsert()
    {
        var sourceId = Guid.NewGuid();
        var listed = Guid.NewGuid();
        var other = Guid.NewGuid();
        IngestRecommendationEventCommand command = Command(sourceId, other, candidates: [listed], slot: 0);

        await ShouldSucceedWithoutInsert(command);
    }

    [Fact]
    public async Task Handle_WhenTargetIsHidden_ShouldSucceedWithoutInsert()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(sourceId, targetId));
    }

    [Fact]
    public async Task Handle_WhenActorOwnsSource_ShouldSucceedWithoutInsert()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(authorId);
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        await ShouldSucceedWithoutInsert(Command(sourceId, targetId, actorUserId: authorId));
    }

    [Fact]
    public async Task Handle_WhenActorOwnsTarget_ShouldSucceedWithoutInsert()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(authorId);

        await ShouldSucceedWithoutInsert(Command(sourceId, targetId, actorUserId: authorId));
    }

    [Fact]
    public async Task Handle_WhenStoreThrows_ShouldSucceedWithoutSurfacing()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _store.TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("db"));

        Result result = await _handler.Handle(Command(sourceId, targetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenPersonalVersionWithMatchingActor_ShouldInsertWithActor()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        Result result = await _handler.Handle(
            Command(sourceId, targetId, actorUserId: actorId, rankingVersion: RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        RecommendationEvent inserted = CapturedEvent();
        inserted.ActorUserId.Should().Be(actorId);
        _signer.Received(1).TryVerify(
            Arg.Is<RecommendationExposurePayload>(p => p.AudienceUserId == actorId),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenPersonalVersionAnonymous_ShouldSucceedWithoutVerifyOrInsert()
    {
        Result result = await _handler.Handle(
            Command(Guid.NewGuid(), Guid.NewGuid(), rankingVersion: RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _signer.DidNotReceive().TryVerify(Arg.Any<RecommendationExposurePayload>(), Arg.Any<string>());
        await _store.DidNotReceive().TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPersonalVersionWithForeignActor_ShouldSucceedWithoutInsert()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        // The token was issued for a different audience, so verification fails.
        _signer.TryVerify(Arg.Any<RecommendationExposurePayload>(), Arg.Any<string>()).Returns(false);

        Result result = await _handler.Handle(
            Command(sourceId, targetId, actorUserId: Guid.NewGuid(), rankingVersion: RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _store.DidNotReceive().TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPublicVersionWithAuthenticatedActor_ShouldVerifyWithoutAudience()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _assetStore.GetPublicAnalyticsSellerId(sourceId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _assetStore.GetPublicAnalyticsSellerId(targetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        Result result = await _handler.Handle(
            Command(sourceId, targetId, actorUserId: Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>());
        _signer.Received(1).TryVerify(
            Arg.Is<RecommendationExposurePayload>(p => p.AudienceUserId == null),
            Arg.Any<string>());
    }

    private async Task ShouldSucceedWithoutInsert(IngestRecommendationEventCommand command)
    {
        Result result = await _handler.Handle(command, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        await _store.DidNotReceive().TryInsert(Arg.Any<RecommendationEvent>(), Arg.Any<CancellationToken>());
    }

    private RecommendationEvent CapturedEvent()
    {
        return _store.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<RecommendationEvent>()
            .Single();
    }

    private static IngestRecommendationEventCommand Command(
        Guid sourceAssetId,
        Guid targetAssetId,
        Guid? actorUserId = null,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<Guid>? candidates = null,
        int slot = 0,
        string? rankingVersion = null)
    {
        IReadOnlyList<Guid> candidateIds = candidates ?? [targetAssetId];
        var request = new IngestRecommendationEventRequest(
            Guid.NewGuid(),
            RecommendationEventType.IMPRESSION,
            Guid.NewGuid(),
            Guid.NewGuid(),
            sourceAssetId,
            targetAssetId,
            slot,
            Guid.NewGuid(),
            rankingVersion ?? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            expiresAt ?? _now.AddMinutes(15),
            new string('a', RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH),
            candidateIds,
            AnalyticsDeviceClass.DESKTOP);
        return new IngestRecommendationEventCommand(request, actorUserId);
    }
}
