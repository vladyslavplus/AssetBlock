using Ardalis.Result;
using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
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
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
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

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsAuthor_ShouldReturnCannotReviewOwnAssetError()
    {
        var userId = Guid.NewGuid();
        var command = new CreateReviewCommand(Guid.NewGuid(), userId, 5, "Great");

        var asset = new Asset { Id = command.AssetId, AuthorId = userId, CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET);
    }

    [Fact]
    public async Task Handle_WhenNotPurchased_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_PURCHASED);
    }

    [Fact]
    public async Task Handle_WhenPurchaseExpired_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);

        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, AssetVersionId = Guid.NewGuid(), OrderLineId = Guid.NewGuid(), PurchasedAt = DateTimeOffset.UtcNow.AddDays(-15) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED);
    }

    [Fact]
    public async Task Handle_WhenReviewAlreadyExists_ShouldReturnError()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, AssetVersionId = Guid.NewGuid(), OrderLineId = Guid.NewGuid(), PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(true);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_REVIEW_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldCreateReviewEnqueueNotificationAndRemoveCache()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, AssetVersionId = Guid.NewGuid(), OrderLineId = Guid.NewGuid(), PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
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
                Arg.Any<Review>(),
                Arg.Any<CancellationToken>())
            .Returns(review);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _reviewStoreMock.Received(1).Create(Arg.Is<Review>(r => r.AssetId == command.AssetId && r.UserId == command.UserId && r.Rating == command.Rating), Arg.Any<CancellationToken>());
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
                && !string.IsNullOrEmpty(e.ResourceId)
                && e.Metadata != null
                && !e.Metadata.ContainsKey("comment")),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(Arg.Is<string>(s => s.StartsWith(CacheKeys.REVIEWS_LIST_PREFIX)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCreateThrows_ShouldReturnBadRequest()
    {
        var command = new CreateReviewCommand(Guid.NewGuid(), Guid.NewGuid(), 5, "Great");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "A" };
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        var purchase = new Purchase { Id = Guid.NewGuid(), UserId = command.UserId, AssetId = command.AssetId, AssetVersionId = Guid.NewGuid(), OrderLineId = Guid.NewGuid(), PurchasedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _purchaseStoreMock.GetPurchase(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _reviewStoreMock.Exists(command.UserId, command.AssetId, Arg.Any<CancellationToken>()).Returns(false);
        _reviewStoreMock.Create(Arg.Any<Review>(), Arg.Any<CancellationToken>()).ThrowsAsync(new Exception("DB Error"));

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_CREATE_FAILED);
    }

    [Fact]
    public void Review_CreateForPurchase_WhenReviewingOwnAsset_ReturnsCannotReviewOwnAsset()
    {
        var authorId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ReviewCreationResult result = Review.CreateForPurchase(Guid.NewGuid(), authorId, authorId, now.AddDays(-1), 5, "Comment", now);

        result.IsSuccess.Should().BeFalse();
        result.IsOwnAsset.Should().BeTrue();
        result.IsPurchaseWindowExpired.Should().BeFalse();
        result.Review.Should().BeNull();
    }

    [Fact]
    public void Review_CreateForPurchase_WhenPurchaseExpired_ReturnsPurchaseWindowExpired()
    {
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset purchasedAt = now.AddDays(-15);

        ReviewCreationResult result = Review.CreateForPurchase(Guid.NewGuid(), authorId, buyerId, purchasedAt, 5, "Comment", now);

        result.IsSuccess.Should().BeFalse();
        result.IsOwnAsset.Should().BeFalse();
        result.IsPurchaseWindowExpired.Should().BeTrue();
        result.Review.Should().BeNull();
    }

    [Fact]
    public void Review_CreateForPurchase_WhenValid_ReturnsSuccessWithPopulatedReview()
    {
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset purchasedAt = now.AddDays(-1);

        ReviewCreationResult result = Review.CreateForPurchase(assetId, authorId, buyerId, purchasedAt, 4, "Nice!", now);

        result.IsSuccess.Should().BeTrue();
        result.IsOwnAsset.Should().BeFalse();
        result.IsPurchaseWindowExpired.Should().BeFalse();
        result.Review.Should().NotBeNull();
        result.Review!.AssetId.Should().Be(assetId);
        result.Review.UserId.Should().Be(buyerId);
        result.Review.Rating.Should().Be(4);
        result.Review.Comment.Should().Be("Nice!");
        result.Review.CreatedAt.Should().Be(now);
    }
}
