using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class CreateCheckoutSessionCommandHandlerTests
{
    private readonly IPaymentService _paymentServiceMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly ICheckoutIntentStore _checkoutIntentStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly CreateCheckoutSessionCommandHandler _handler;

    public CreateCheckoutSessionCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _purchaseStoreMock.GetPurchase(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Purchase?)null);
        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new CreateCheckoutSessionCommandHandler(
            _paymentServiceMock,
            _assetStoreMock,
            _purchaseStoreMock,
            _checkoutIntentStoreMock,
            _unitOfWorkMock,
            NullLogger<CreateCheckoutSessionCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns((AssetCurrentVersionSnapshot?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenDelisted_ShouldReturnAssetNotFound()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        var snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid(), deletedAt: DateTimeOffset.UtcNow);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotPurchaseOwnAssetError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        var snapshot = CreateSnapshot(command.AssetId, authorId: userId);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenAlreadyPurchased_ShouldReturnAlreadyPurchasedError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId);
        var snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = command.AssetId,
            AssetVersionId = Guid.NewGuid(),
            CheckoutIntentId = Guid.NewGuid(),
            PricePaid = 9.99m,
            Currency = "usd",
            StripePaymentId = "cs_test_1",
            PurchasedAt = DateTimeOffset.UtcNow
        };
        _purchaseStoreMock.GetPurchase(userId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        await _paymentServiceMock.DidNotReceiveWithAnyArgs().CreateCheckoutSession(
            Arg.Any<CheckoutLineItem>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldReturnPaymentError()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        var snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid());
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutLineItem>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Stripe unavailable"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_PAYMENT_FAILED);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnCheckoutUrl()
    {
        const string sessionUrl = "https://checkout.stripe.com/pay/session_123";
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        var snapshot = CreateSnapshot(command.AssetId, Guid.NewGuid(), price: 29.99m);
        _assetStoreMock.GetCurrentVersionSnapshot(command.AssetId, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<CheckoutLineItem>(), command.UserId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSession("cs_test_123", sessionUrl));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
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
            FileName: "asset.zip",
            StorageKey: "assets/key",
            ContentLength: 1024,
            ContentSha256: new string('a', 64));
}
