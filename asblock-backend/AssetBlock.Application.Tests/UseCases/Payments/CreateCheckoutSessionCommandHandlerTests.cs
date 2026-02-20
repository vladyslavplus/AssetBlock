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
    private readonly CreateCheckoutSessionCommandHandler _handler;

    public CreateCheckoutSessionCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();

        _handler = new CreateCheckoutSessionCommandHandler(
            _paymentServiceMock,
            _assetStoreMock,
            NullLogger<CreateCheckoutSessionCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnError()
    {
        // Arrange
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid(), "https://ok", "https://cancel");
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotPurchaseOwnAssetError()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), userId, "https://ok", "https://cancel");
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = userId, CategoryId = Guid.NewGuid(),
            Title = "Asset", Price = 9.99m, StorageKey = "k", FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldReturnPaymentError()
    {
        // Arrange
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid(), "https://ok", "https://cancel");
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(),
            Title = "T", Price = 9.99m, StorageKey = "k", FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _paymentServiceMock.CreateCheckoutSession(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Stripe unavailable"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_PAYMENT_FAILED);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnCheckoutUrl()
    {
        // Arrange
        const string sessionUrl = "https://checkout.stripe.com/pay/session_123";
        var command = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid(), "https://ok", "https://cancel");
        var asset = new Asset
        {
            Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(),
            Title = "Asset", Price = 29.99m, StorageKey = "key", FileName = "asset.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _paymentServiceMock.CreateCheckoutSession(
                command.AssetId, command.UserId, command.SuccessUrl, command.CancelUrl, Arg.Any<CancellationToken>())
            .Returns(sessionUrl);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be(sessionUrl);
    }
}
