using AssetBlock.Application.UseCases.Payments.Checkout;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class CheckoutAttributionNormalizerTests
{
    private readonly ICollectionStore _collectionStoreMock = Substitute.For<ICollectionStore>();
    private readonly CheckoutAttributionNormalizer _normalizer;

    public CheckoutAttributionNormalizerTests()
    {
        _normalizer = new CheckoutAttributionNormalizer(
            _collectionStoreMock,
            NullLogger<CheckoutAttributionNormalizer>.Instance);
    }

    [Fact]
    public async Task TryNormalize_WhenRequestIsNull_ShouldReturnNull()
    {
        var snapshot = await _normalizer.TryNormalize(null, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenSourceIsNull_ShouldReturnNull()
    {
        var request = new CheckoutAttributionRequest(null, Guid.NewGuid(), "example.com");

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenSourceIsNotAKnownEnumValue_ShouldReturnNull()
    {
        var request = new CheckoutAttributionRequest((AnalyticsTrafficSource)77, null, null);

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenCollectionSourceIsVerified_ShouldKeepCollectionId()
    {
        var assetId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, assetId, Arg.Any<CancellationToken>())
            .Returns(sellerId);
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null);

        var snapshot = await _normalizer.TryNormalize(
            request,
            assetId,
            sellerId,
            visitorId,
            sessionId);

        snapshot.Should().NotBeNull();
        snapshot.Source.Should().Be(AnalyticsTrafficSource.COLLECTION);
        snapshot.CollectionId.Should().Be(collectionId);
        snapshot.ReferrerHost.Should().BeNull();
        snapshot.VisitorId.Should().Be(visitorId);
        snapshot.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task TryNormalize_WhenCollectionBelongsToAnotherSeller_ShouldReturnNull()
    {
        var assetId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, assetId, Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null);

        var snapshot = await _normalizer.TryNormalize(
            request,
            assetId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenCollectionIsNotPublishedOrAssetIsNotAMember_ShouldReturnNull()
    {
        var assetId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, assetId, Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null);

        var snapshot = await _normalizer.TryNormalize(request, assetId, Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenCollectionSourceHasNoCollectionId_ShouldReturnNull()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, null, null);

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenCollectionSourceIsUsedForBundleCheckout_ShouldReturnNull()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, Guid.NewGuid(), null);

        var snapshot = await _normalizer.TryNormalize(request, assetId: null, Guid.NewGuid());

        snapshot.Should().BeNull();
        await _collectionStoreMock.DidNotReceiveWithAnyArgs()
            .GetPublishedMemberSellerId(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryNormalize_WhenNonCollectionSourceCarriesCollectionId_ShouldReturnNull()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.SEARCH, Guid.NewGuid(), null);

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenExternalSourceHasReferrerUrl_ShouldKeepBareHost()
    {
        var request = new CheckoutAttributionRequest(
            AnalyticsTrafficSource.EXTERNAL,
            null,
            "https://Blog.Example.com/post?utm_source=x");

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().NotBeNull();
        snapshot.Source.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        snapshot.ReferrerHost.Should().Be("blog.example.com");
        snapshot.CollectionId.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenExternalReferrerIsUnparseable_ShouldKeepSourceWithoutHost()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.EXTERNAL, null, "not a host");

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().NotBeNull();
        snapshot.Source.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        snapshot.ReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenSourceIsNotExternal_ShouldDropReferrerHost()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.CATALOG, null, "blog.example.com");

        var snapshot = await _normalizer.TryNormalize(request, Guid.NewGuid(), Guid.NewGuid());

        snapshot.Should().NotBeNull();
        snapshot.Source.Should().Be(AnalyticsTrafficSource.CATALOG);
        snapshot.ReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenVisitorOrSessionIsEmptyGuid_ShouldTreatAsNull()
    {
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.CATALOG, null, null);

        var snapshot = await _normalizer.TryNormalize(
            request,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty);

        snapshot.Should().NotBeNull();
        snapshot.VisitorId.Should().BeNull();
        snapshot.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task TryNormalize_WhenStoreThrows_ShouldReturnNull()
    {
        var assetId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, assetId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var request = new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null);

        var snapshot = await _normalizer.TryNormalize(request, assetId, Guid.NewGuid());

        snapshot.Should().BeNull();
    }
}
