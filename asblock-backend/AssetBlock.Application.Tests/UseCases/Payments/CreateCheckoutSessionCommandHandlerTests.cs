using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.Checkout;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class CreateCheckoutSessionCommandHandlerTests
{
    private readonly IPaymentService _paymentServiceMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly ICheckoutIntentStore _checkoutIntentStoreMock;
    private readonly ICollectionStore _collectionStoreMock;
    private readonly CreateCheckoutSessionCommandHandler _handler;

    public CreateCheckoutSessionCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        _collectionStoreMock = Substitute.For<ICollectionStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _purchaseStoreMock.GetPurchase(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Purchase?)null);
        _checkoutIntentStoreMock.GetPendingForAsset(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CheckoutIntent?)null);
        _checkoutIntentStoreMock.TrySetStripeSessionId(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _assetStoreMock.GetForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Asset
            {
                Id = ci.Arg<Guid>(),
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "locked"
            });
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        var orchestrator = new CheckoutSessionOrchestrator(
            _paymentServiceMock,
            _checkoutIntentStoreMock,
            unitOfWorkMock,
            NullLogger<CheckoutSessionOrchestrator>.Instance);

        _handler = new CreateCheckoutSessionCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            _checkoutIntentStoreMock,
            orchestrator,
            new CheckoutAttributionNormalizer(_collectionStoreMock, NullLogger<CheckoutAttributionNormalizer>.Instance));
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns((AssetCurrentVersionSnapshot?)null);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenDelisted_ShouldReturnAssetNotFound()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid(), deletedAt: DateTimeOffset.UtcNow);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotPurchaseOwnAssetError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, authorId: userId);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenAlreadyPurchased_ShouldReturnAlreadyPurchasedError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = command.AssetId,
            AssetVersionId = Guid.NewGuid(),
            OrderLineId = Guid.NewGuid(),
            PurchasedAt = DateTimeOffset.UtcNow
        };
        _purchaseStoreMock.GetPurchase(userId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        await _paymentServiceMock.DidNotReceiveWithAnyArgs().CreateCheckoutSession(
            Arg.Any<CheckoutSessionDraft>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldReturnPaymentError()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutSessionDraft>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Stripe unavailable"));

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_PAYMENT_FAILED);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnCheckoutUrl()
    {
        const string sessionUrl = "https://checkout.stripe.com/pay/session_123";
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid(), price: 29.99m);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutSessionDraft>(), Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSession("cs_test_123", sessionUrl));

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
    }

    [Fact]
    public async Task Handle_WhenPendingIntentHasNoStripeSession_ShouldResumeSameIntent()
    {
        const string sessionUrl = "https://checkout.stripe.com/pay/resumed";
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        CheckoutIntent pendingIntent = CreatePendingIntent(command.UserId, snapshot);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _checkoutIntentStoreMock.GetPendingForAsset(command.UserId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(pendingIntent);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Is<CheckoutSessionDraft>(d => d.CheckoutIntentId == pendingIntent.Id),
                Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSession("cs_test_resumed", sessionUrl));

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
        await _checkoutIntentStoreMock.DidNotReceiveWithAnyArgs().CreateWithItemsAndReservations(
            Arg.Any<CheckoutIntent>(),
            Arg.Any<IReadOnlyList<CheckoutIntentItem>>(),
            Arg.Any<IReadOnlyList<CheckoutReservation>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPendingStripeSessionIsOpen_ShouldReturnExistingUrl()
    {
        const string sessionUrl = "https://checkout.stripe.com/pay/existing";
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        CheckoutIntent pendingIntent = CreatePendingIntent(command.UserId, snapshot);
        pendingIntent.StripeSessionId = "cs_test_existing";
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _checkoutIntentStoreMock.GetPendingForAsset(command.UserId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(pendingIntent);
        _paymentServiceMock.GetCheckoutSession("cs_test_existing", Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(
                "cs_test_existing",
                StripeConstants.CheckoutSessionStatuses.OPEN,
                sessionUrl));

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
        await _paymentServiceMock.DidNotReceiveWithAnyArgs().CreateCheckoutSession(
            Arg.Any<CheckoutSessionDraft>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExternalAttribution_ShouldPersistNormalizedReferrerHost()
    {
        var visitorId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CheckoutAttributionRequest(
                AnalyticsTrafficSource.EXTERNAL,
                CollectionId: null,
                "https://Blog.Example.com:443/posts/1?utm_source=x"),
            visitorId,
            sessionId);
        ArrangeNewCheckoutSession(command.AssetId);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CheckoutIntent intent = CapturedCreatedIntent();
        intent.AttributionSource.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        intent.AttributionReferrerHost.Should().Be("blog.example.com");
        intent.AttributionCollectionId.Should().BeNull();
        intent.AnalyticsVisitorId.Should().Be(visitorId);
        intent.AnalyticsSessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task Handle_WhenCollectionAttributionIsVerified_ShouldPersistCollectionId()
    {
        var authorId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, ReferrerHost: null));
        ArrangeNewCheckoutSession(command.AssetId, authorId);
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(authorId);

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CheckoutIntent intent = CapturedCreatedIntent();
        intent.AttributionSource.Should().Be(AnalyticsTrafficSource.COLLECTION);
        intent.AttributionCollectionId.Should().Be(collectionId);
        intent.AttributionReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenCollectionAttributionBelongsToAnotherSeller_ShouldDropAttribution()
    {
        var collectionId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, ReferrerHost: null));
        ArrangeNewCheckoutSession(command.AssetId);
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CheckoutIntent intent = CapturedCreatedIntent();
        intent.AttributionSource.Should().BeNull();
        intent.AttributionCollectionId.Should().BeNull();
        intent.AttributionReferrerHost.Should().BeNull();
        intent.AnalyticsVisitorId.Should().BeNull();
        intent.AnalyticsSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenAttributionIsInvalidButVisitorIdsProvided_ShouldDropAllAttributionFields()
    {
        var collectionId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, ReferrerHost: null),
            AnalyticsVisitorId: Guid.NewGuid(),
            AnalyticsSessionId: Guid.NewGuid());
        ArrangeNewCheckoutSession(command.AssetId);
        _collectionStoreMock
            .GetPublishedMemberSellerId(collectionId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        CheckoutIntent intent = CapturedCreatedIntent();
        intent.AttributionSource.Should().BeNull();
        intent.AnalyticsVisitorId.Should().BeNull();
        intent.AnalyticsSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenResumingPendingIntent_ShouldNotReattributeCheckout()
    {
        var command = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CheckoutAttributionRequest(AnalyticsTrafficSource.SEARCH, CollectionId: null, ReferrerHost: null));
        AssetCurrentVersionSnapshot snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        CheckoutIntent pendingIntent = CreatePendingIntent(command.UserId, snapshot);
        pendingIntent.AttributionSource = AnalyticsTrafficSource.CATALOG;
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _checkoutIntentStoreMock.GetPendingForAsset(command.UserId, command.AssetId, Arg.Any<CancellationToken>())
            .Returns(pendingIntent);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutSessionDraft>(),
                Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSession("cs_test_resumed", "https://checkout.stripe.com/pay/resumed"));

        Result<CreateCheckoutSessionResponse> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        pendingIntent.AttributionSource.Should().Be(AnalyticsTrafficSource.CATALOG);
        await _checkoutIntentStoreMock.DidNotReceiveWithAnyArgs().CreateWithItemsAndReservations(
            Arg.Any<CheckoutIntent>(),
            Arg.Any<IReadOnlyList<CheckoutIntentItem>>(),
            Arg.Any<IReadOnlyList<CheckoutReservation>>(),
            Arg.Any<CancellationToken>());
    }

    private void ArrangeNewCheckoutSession(Guid assetId, Guid? authorId = null)
    {
        _assetStoreMock.GetCurrentVersionSnapshot(assetId, Arg.Any<CancellationToken>())
            .Returns(CreateSnapshot(assetId, authorId ?? Guid.NewGuid()));
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutSessionDraft>(),
                Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSession("cs_test_attribution", "https://checkout.stripe.com/pay/attribution"));
    }

    private CheckoutIntent CapturedCreatedIntent()
    {
        ICall call = _checkoutIntentStoreMock.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICheckoutIntentStore.CreateWithItemsAndReservations));
        return (CheckoutIntent)call.GetArguments()[0]!;
    }

    private static CheckoutIntent CreatePendingIntent(Guid userId, AssetCurrentVersionSnapshot snapshot)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();
        return new CheckoutIntent
        {
            Id = intentId,
            UserId = userId,
            AssetId = snapshot.AssetId,
            ProductTitle = snapshot.Title,
            AmountTotal = snapshot.Price,
            Currency = StripeConstants.CURRENCY_USD,
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
            Items =
            [
                new CheckoutIntentItem
                {
                    Id = Guid.NewGuid(),
                    CheckoutIntentId = intentId,
                    AssetId = snapshot.AssetId,
                    AssetVersionId = snapshot.AssetVersionId,
                    SellerId = snapshot.AuthorId,
                    Position = 1,
                    AssetTitleSnapshot = snapshot.Title,
                    VersionNumber = snapshot.VersionNumber,
                    ListPrice = snapshot.Price,
                    AllocatedPrice = snapshot.Price,
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = snapshot.LicenseTemplateVersion,
                    LicenseDisplayName = snapshot.LicenseDisplayName,
                    LicenseTerms = snapshot.LicenseTerms
                }
            ]
        };
    }

    private static AssetCurrentVersionSnapshot CreateSnapshot(
        Guid assetId,
        Guid? authorId = null,
        DateTimeOffset? deletedAt = null,
        decimal price = 9.99m) =>
        new(
            AssetId: assetId,
            AssetVersionId: Guid.NewGuid(),
            AuthorId: authorId ?? Guid.NewGuid(),
            Title: "Test Asset",
            Description: null,
            Price: price,
            DeletedAt: deletedAt,
            VersionNumber: 1,
            VersionCreatedAt: DateTimeOffset.UtcNow,
            FileName: "asset.zip",
            StorageKey: "assets/key",
            ContentLength: 1024,
            ContentSha256: new string('a', 64),
            LicenseCode: "PERSONAL",
            LicenseTemplateVersion: "1.0",
            LicenseDisplayName: "Personal use",
            LicenseTerms: "terms");
}
