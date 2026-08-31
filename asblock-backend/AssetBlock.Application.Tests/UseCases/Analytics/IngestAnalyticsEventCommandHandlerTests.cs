using Ardalis.Result;
using AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Analytics;

public class IngestAnalyticsEventCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock = Substitute.For<IAssetStore>();
    private readonly IBundleStore _bundleStoreMock = Substitute.For<IBundleStore>();
    private readonly ICollectionStore _collectionStoreMock = Substitute.For<ICollectionStore>();
    private readonly IAnalyticsEventStore _analyticsEventStoreMock = Substitute.For<IAnalyticsEventStore>();
    private readonly IngestAnalyticsEventCommandHandler _handler;

    public IngestAnalyticsEventCommandHandlerTests()
    {
        _analyticsEventStoreMock.TryInsert(Arg.Any<AnalyticsEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        _handler = new IngestAnalyticsEventCommandHandler(
            _assetStoreMock,
            _bundleStoreMock,
            _collectionStoreMock,
            _analyticsEventStoreMock,
            NullLogger<IngestAnalyticsEventCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetIsPublic_ShouldInsertEventForAuthor()
    {
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(authorId);
        IngestAnalyticsEventCommand command = Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AnalyticsEvent inserted = CapturedEvent();
        inserted.SellerId.Should().Be(authorId);
        inserted.AssetId.Should().Be(assetId);
        inserted.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Handle_WhenAssetIsSoftDeleted_ShouldSucceedWithoutInsert()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId));
    }

    [Fact]
    public async Task Handle_WhenAssetDoesNotExist_ShouldSucceedWithoutInsert()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId));
    }

    [Fact]
    public async Task Handle_WhenSellerViewsOwnAsset_ShouldSucceedWithoutInsert()
    {
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(authorId);

        await ShouldSucceedWithoutInsert(Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId, actorUserId: authorId));
    }

    [Fact]
    public async Task Handle_WhenBundleIsNotPubliclyAvailable_ShouldSucceedWithoutInsert()
    {
        var bundleId = Guid.NewGuid();
        _bundleStoreMock.GetPublicAnalyticsSellerId(bundleId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(AnalyticsEventType.BUNDLE_VIEW, bundleId: bundleId));
    }

    [Fact]
    public async Task Handle_WhenCollectionIsNotPublished_ShouldSucceedWithoutInsert()
    {
        var collectionId = Guid.NewGuid();
        _collectionStoreMock.GetPublishedSellerId(collectionId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(AnalyticsEventType.COLLECTION_VIEW, collectionId: collectionId));
    }

    [Fact]
    public async Task Handle_WhenCollectionViewIsPublished_ShouldInsertEventForSeller()
    {
        var collectionId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        _collectionStoreMock.GetPublishedSellerId(collectionId, Arg.Any<CancellationToken>()).Returns(sellerId);

        Result result = await _handler.Handle(
            Command(AnalyticsEventType.COLLECTION_VIEW, collectionId: collectionId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CapturedEvent().SellerId.Should().Be(sellerId);
    }

    [Fact]
    public async Task Handle_WhenAssetIsNotAMemberOfTheCollection_ShouldSucceedWithoutInsert()
    {
        var collectionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, assetId, Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(
            AnalyticsEventType.COLLECTION_ITEM_CLICK,
            assetId: assetId,
            collectionId: collectionId));
    }

    [Fact]
    public async Task Handle_WhenDownloadHasNoAuthenticatedActor_ShouldSucceedWithoutInsert()
    {
        IngestAnalyticsEventCommand command = Command(
            AnalyticsEventType.DOWNLOAD_REQUESTED,
            assetId: Guid.NewGuid(),
            assetVersionId: Guid.NewGuid());

        await ShouldSucceedWithoutInsert(command);
        await _assetStoreMock.DidNotReceiveWithAnyArgs().ResolveDownloadAnalyticsSellerId(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDownloadIsNotEntitled_ShouldSucceedWithoutInsert()
    {
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        _assetStoreMock
            .ResolveDownloadAnalyticsSellerId(assetId, versionId, actorUserId, Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(
            AnalyticsEventType.DOWNLOAD_REQUESTED,
            assetId: assetId,
            assetVersionId: versionId,
            actorUserId: actorUserId));
    }

    [Fact]
    public async Task Handle_WhenAuthorDownloadsOwnAsset_ShouldSucceedWithoutInsert()
    {
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        _assetStoreMock
            .ResolveDownloadAnalyticsSellerId(assetId, versionId, authorId, Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        await ShouldSucceedWithoutInsert(Command(
            AnalyticsEventType.DOWNLOAD_REQUESTED,
            assetId: assetId,
            assetVersionId: versionId,
            actorUserId: authorId));
    }

    [Fact]
    public async Task Handle_WhenBuyerDownloadIsEntitled_ShouldInsertEvent()
    {
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        _assetStoreMock
            .ResolveDownloadAnalyticsSellerId(assetId, versionId, buyerId, Arg.Any<CancellationToken>())
            .Returns(authorId);

        Result result = await _handler.Handle(
            Command(
                AnalyticsEventType.DOWNLOAD_REQUESTED,
                assetId: assetId,
                assetVersionId: versionId,
                actorUserId: buyerId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AnalyticsEvent inserted = CapturedEvent();
        inserted.SellerId.Should().Be(authorId);
        inserted.ActorUserId.Should().Be(buyerId);
        inserted.AssetVersionId.Should().Be(versionId);
    }

    [Fact]
    public async Task Handle_WhenEventIdWasAlreadyStored_ShouldStillSucceed()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _analyticsEventStoreMock.TryInsert(Arg.Any<AnalyticsEvent>(), Arg.Any<CancellationToken>()).Returns(false);

        Result result = await _handler.Handle(
            Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenStoreThrows_ShouldStillSucceed()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _analyticsEventStoreMock.TryInsert(Arg.Any<AnalyticsEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("telemetry storage unavailable"));

        Result result = await _handler.Handle(
            Command(AnalyticsEventType.ASSET_VIEW, assetId: assetId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenExternalSourceHasReferrerUrl_ShouldStoreBareHost()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: assetId) with
            {
                Source = AnalyticsTrafficSource.EXTERNAL,
                ReferrerHost = "https://News.Example.com/a/b?q=secret"
            },
            ActorUserId: null);

        await _handler.Handle(command, CancellationToken.None);

        CapturedEvent().ReferrerHost.Should().Be("news.example.com");
    }

    [Fact]
    public async Task Handle_WhenExternalReferrerIsUnparseable_ShouldStoreNullHost()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: assetId) with
            {
                Source = AnalyticsTrafficSource.EXTERNAL,
                ReferrerHost = "not a host"
            },
            ActorUserId: null);

        await _handler.Handle(command, CancellationToken.None);

        CapturedEvent().ReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenSourceIsNotExternal_ShouldDropReferrerHost()
    {
        var assetId = Guid.NewGuid();
        _assetStoreMock.GetPublicAnalyticsSellerId(assetId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: assetId) with
            {
                Source = AnalyticsTrafficSource.CATALOG,
                ReferrerHost = "news.example.com"
            },
            ActorUserId: null);

        await _handler.Handle(command, CancellationToken.None);

        CapturedEvent().ReferrerHost.Should().BeNull();
    }

    private async Task ShouldSucceedWithoutInsert(IngestAnalyticsEventCommand command)
    {
        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _analyticsEventStoreMock.DidNotReceiveWithAnyArgs().TryInsert(
            Arg.Any<AnalyticsEvent>(),
            Arg.Any<CancellationToken>());
    }

    private AnalyticsEvent CapturedEvent()
    {
        ICall call = _analyticsEventStoreMock.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAnalyticsEventStore.TryInsert));
        return (AnalyticsEvent)call.GetArguments()[0]!;
    }

    private static IngestAnalyticsEventCommand Command(
        AnalyticsEventType eventType,
        Guid? assetId = null,
        Guid? assetVersionId = null,
        Guid? bundleId = null,
        Guid? collectionId = null,
        Guid? actorUserId = null) =>
        new(Request(eventType, assetId, assetVersionId, bundleId, collectionId), actorUserId);

    private static IngestAnalyticsEventRequest Request(
        AnalyticsEventType eventType,
        Guid? assetId = null,
        Guid? assetVersionId = null,
        Guid? bundleId = null,
        Guid? collectionId = null) =>
        new(
            Guid.NewGuid(),
            eventType,
            VisitorId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            assetId,
            assetVersionId,
            bundleId,
            collectionId,
            AnalyticsTrafficSource.CATALOG,
            ReferrerHost: null,
            AnalyticsDeviceClass.DESKTOP);
}
