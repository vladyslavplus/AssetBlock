using AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;

namespace AssetBlock.Application.Tests.UseCases.Analytics;

public class IngestAnalyticsEventCommandValidatorTests
{
    private readonly IngestAnalyticsEventCommandValidator _validator = new();

    [Fact]
    public void Validate_WhenAssetViewCarriesOnlyAssetId_ShouldPass()
    {
        var result = _validator.Validate(Command(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBundleViewCarriesOnlyBundleId_ShouldPass()
    {
        var result = _validator.Validate(Command(AnalyticsEventType.BUNDLE_VIEW, bundleId: Guid.NewGuid()));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenCollectionViewCarriesOnlyCollectionId_ShouldPass()
    {
        var result = _validator.Validate(Command(AnalyticsEventType.COLLECTION_VIEW, collectionId: Guid.NewGuid()));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenCollectionItemClickCarriesCollectionAndAsset_ShouldPass()
    {
        var command = Command(
            AnalyticsEventType.COLLECTION_ITEM_CLICK,
            assetId: Guid.NewGuid(),
            collectionId: Guid.NewGuid());

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenDownloadRequestedCarriesAssetAndVersion_ShouldPass()
    {
        var command = Command(
            AnalyticsEventType.DOWNLOAD_REQUESTED,
            assetId: Guid.NewGuid(),
            assetVersionId: Guid.NewGuid());

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEventIdIsEmpty_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, eventId: Guid.Empty, assetId: Guid.NewGuid()),
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenVisitorIdIsEmpty_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with { VisitorId = Guid.Empty },
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenSessionIdIsEmpty_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with { SessionId = Guid.Empty },
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenAssetViewHasNoTarget_ShouldFail()
    {
        ShouldFailWithEventInvalid(Command(AnalyticsEventType.ASSET_VIEW));
    }

    [Fact]
    public void Validate_WhenAssetViewAlsoCarriesBundleId_ShouldFail()
    {
        ShouldFailWithEventInvalid(Command(
            AnalyticsEventType.ASSET_VIEW,
            assetId: Guid.NewGuid(),
            bundleId: Guid.NewGuid()));
    }

    [Fact]
    public void Validate_WhenCollectionItemClickHasNoAssetId_ShouldFail()
    {
        ShouldFailWithEventInvalid(Command(
            AnalyticsEventType.COLLECTION_ITEM_CLICK,
            collectionId: Guid.NewGuid()));
    }

    [Fact]
    public void Validate_WhenDownloadRequestedHasNoVersionId_ShouldFail()
    {
        ShouldFailWithEventInvalid(Command(AnalyticsEventType.DOWNLOAD_REQUESTED, assetId: Guid.NewGuid()));
    }

    [Fact]
    public void Validate_WhenSourceIsNotAKnownEnumValue_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with
            {
                Source = (AnalyticsTrafficSource)99
            },
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenDeviceClassIsNotAKnownEnumValue_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with
            {
                DeviceClass = (AnalyticsDeviceClass)42
            },
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenReferrerHostExceedsMaxLength_ShouldFail()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with
            {
                Source = AnalyticsTrafficSource.EXTERNAL,
                ReferrerHost = new string('a', AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH + 1)
            },
            ActorUserId: null);

        ShouldFailWithEventInvalid(command);
    }

    [Fact]
    public void Validate_WhenExternalSourceHasNoReferrerHost_ShouldPass()
    {
        var command = new IngestAnalyticsEventCommand(
            Request(AnalyticsEventType.ASSET_VIEW, assetId: Guid.NewGuid()) with
            {
                Source = AnalyticsTrafficSource.EXTERNAL
            },
            ActorUserId: null);

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    private void ShouldFailWithEventInvalid(IngestAnalyticsEventCommand command)
    {
        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID));
    }

    private static IngestAnalyticsEventCommand Command(
        AnalyticsEventType eventType,
        Guid? assetId = null,
        Guid? assetVersionId = null,
        Guid? bundleId = null,
        Guid? collectionId = null) =>
        new(Request(eventType, Guid.NewGuid(), assetId, assetVersionId, bundleId, collectionId), ActorUserId: null);

    private static IngestAnalyticsEventRequest Request(
        AnalyticsEventType eventType,
        Guid? eventId = null,
        Guid? assetId = null,
        Guid? assetVersionId = null,
        Guid? bundleId = null,
        Guid? collectionId = null) =>
        new(
            eventId ?? Guid.NewGuid(),
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
