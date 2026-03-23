using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class CreateReviewCommandHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly IRealtimeNotificationPublisher _notificationsMock;
    private readonly CreateReviewCommandHandler _handler;

    public CreateReviewCommandHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _notificationsMock = Substitute.For<IRealtimeNotificationPublisher>();

        _handler = new CreateReviewCommandHandler(
            _reviewStoreMock,
            _purchaseStoreMock,
            _assetStoreMock,
            _cacheMock,
            _notificationsMock,
            NullLogger<CreateReviewCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnError()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotReviewOwnAssetError()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var command = new CreateReviewCommand(Guid.NewGuid(), userId, 5, "Great");

        var asset = new Asset { Id = command.AssetId, AuthorId = userId, CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenNotPurchased_ShouldReturnError()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_PURCHASED);
    }

    [Fact]
    public async Task Handle_WhenPurchaseExpired_ShouldReturnError()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-15) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED);
    }

    [Fact]
    public async Task Handle_WhenReviewAlreadyExists_ShouldReturnError()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldCreateReviewAndRemoveCache()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _reviewStoreMock.Received(1).Create(command.AssetId, command.UserId, command.Rating, command.Comment, Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(Arg.Is<string>(s => s.StartsWith(CacheKeys.REVIEWS_LIST_PREFIX)), Arg.Any<CancellationToken>());
        await _notificationsMock.Received(1).NotifyReviewReceived(
            asset.AuthorId,
            command.AssetId,
            "A",
            command.UserId,
            command.Rating,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCreateThrows_ShouldReturnBadRequest()
    {
        // Arrange
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(false);
        _reviewStoreMock.Create(command.AssetId, command.UserId, command.Rating, command.Comment, Arg.Any<CancellationToken>()).ThrowsAsync(new Exception("DB Error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_CREATE_FAILED);
    }
}
