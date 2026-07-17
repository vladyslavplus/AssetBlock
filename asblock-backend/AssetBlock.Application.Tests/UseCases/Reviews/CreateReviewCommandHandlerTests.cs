using Ardalis.Result;
using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
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
    private readonly IOutboxStore _outboxStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly CreateReviewCommandHandler _handler;

    public CreateReviewCommandHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new CreateReviewCommandHandler(
            _reviewStoreMock,
            _purchaseStoreMock,
            _assetStoreMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<CreateReviewCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotReviewOwnAssetError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateReviewCommand(Guid.NewGuid(), userId, 5, "Great");

        var asset = new Asset { Id = command.AssetId, AuthorId = userId, CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenNotPurchased_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_PURCHASED);
    }

    [Fact]
    public async Task Handle_WhenPurchaseExpired_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-15) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED);
    }

    [Fact]
    public async Task Handle_WhenReviewAlreadyExists_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_REVIEW_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldCreateReviewEnqueueNotificationAndRemoveCache()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(false);
        var review = new Review
        {
            Id = Guid.NewGuid(),
            AssetId = command.AssetId,
            UserId = command.UserId,
            Rating = command.Rating,
            Comment = command.Comment
        };
        _reviewStoreMock.Create(
                command.AssetId,
                command.UserId,
                command.Rating,
                command.Comment,
                Arg.Any<CancellationToken>())
            .Returns(review);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _reviewStoreMock.Received(1).Create(command.AssetId, command.UserId, command.Rating, command.Comment, Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Is<NotificationDispatchPayload>(p =>
                p.RecipientUserId == asset.AuthorId
                && p.Kind == NotificationKind.REVIEW_RECEIVED
                && p.HubMethod == NotificationHubMethods.REVIEW_RECEIVED),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.REVIEW_CREATE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceType == AuditResourceTypes.REVIEW
                && e.ResourceId == review.Id.ToString()
                && e.Metadata != null
                && !e.Metadata.ContainsKey("comment")),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(Arg.Is<string>(s => s.StartsWith(CacheKeys.REVIEWS_LIST_PREFIX)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCreateThrows_ShouldReturnBadRequest()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, StripePaymentId = "pay_1", PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(false);
        _reviewStoreMock.Create(command.AssetId, command.UserId, command.Rating, command.Comment, Arg.Any<CancellationToken>()).ThrowsAsync(new Exception("DB Error"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_CREATE_FAILED);
    }
}
