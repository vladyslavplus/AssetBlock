using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
    private readonly CreateCheckoutSessionCommandHandler _handler;

    public CreateCheckoutSessionCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _purchaseStoreMock.GetPurchase(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Purchase?)null);

        _handler = new CreateCheckoutSessionCommandHandler(
            _paymentServiceMock,
            _assetStoreMock,
            _purchaseStoreMock,
            NullLogger<CreateCheckoutSessionCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

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
        var asset = new Asset
        {
            Id = command.AssetId,
            AuthorId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Delisted",
            Price = 1m,
            StorageKey = "k",
            FileName = "f",
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = DateTimeOffset.UtcNow,
        };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

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
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = userId, CategoryId = Guid.NewGuid(),
            Title = "Asset", Price = 9.99m, StorageKey = "k", FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

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
        var asset = new Asset
        {
            Id = command.AssetId,
            AuthorId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Asset",
            Price = 9.99m,
            StorageKey = "k",
            FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = command.AssetId,
            StripePaymentId = "cs_test_1",
            PurchasedAt = DateTimeOffset.UtcNow
        };
        _purchaseStoreMock.GetPurchase(userId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        await _paymentServiceMock.DidNotReceiveWithAnyArgs().CreateCheckoutSession(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldReturnPaymentError()
    {
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(),
            Title = "T", Price = 9.99m, StorageKey = "k", FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
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
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(),
            Title = "Asset", Price = 29.99m, StorageKey = "key", FileName = "asset.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _paymentServiceMock.CreateCheckoutSession(
                command.AssetId, command.UserId, Arg.Any<CancellationToken>())
            .Returns(sessionUrl);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
    }
}
