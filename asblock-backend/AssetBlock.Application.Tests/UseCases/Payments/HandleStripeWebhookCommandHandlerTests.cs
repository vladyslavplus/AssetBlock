using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class HandleStripeWebhookCommandHandlerTests
{
    private readonly IPaymentService _paymentServiceMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly HandleStripeWebhookCommandHandler _handler;

    public HandleStripeWebhookCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new HandleStripeWebhookCommandHandler(
            _paymentServiceMock,
            _assetStoreMock,
            _purchaseStoreMock,
            _unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceReturnsNull_ShouldReturnSuccessWithNullPayload()
    {
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns((StripeCheckoutCompleted?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldCreatePurchaseWriteAuditAndReturnPayload()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_test_session";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(userId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = assetId,
            AuthorId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Pack",
            StorageKey = "k",
            FileName = "f.zip"
        });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(userId);
        result.Value.AssetId.Should().Be(assetId);

        await _purchaseStoreMock.Received(1).Add(
            Arg.Is<Purchase>(p =>
                p.UserId == userId &&
                p.AssetId == assetId &&
                p.StripePaymentId == sessionId),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.PURCHASE_COMPLETED,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.PAYMENT_PURCHASE_COMPLETED &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceType == AuditResourceTypes.PURCHASE &&
                e.ActorTypeOverride == AuditActorType.USER &&
                e.ActorUserIdOverride == userId &&
                e.Metadata != null && e.Metadata.ContainsKey("assetId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExistingPurchaseBySession_ShouldReturnPayloadWithoutCreatingOrAuditing()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_existing";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(userId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            StripePaymentId = sessionId,
            PurchasedAt = DateTimeOffset.UtcNow
        });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _unitOfWorkMock.DidNotReceiveWithAnyArgs()
            .ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedAndAssetFound_ShouldEnqueueNotificationsForBuyerAndAuthor()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_notify";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(buyerId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = Guid.NewGuid(),
            Title = "Pack",
            StorageKey = "k",
            FileName = "f.zip"
        });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.PURCHASE_COMPLETED,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        // purchase completed + download ready for buyer + asset sold for author
        await _outboxStoreMock.Received(3).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerIsAuthor_ShouldNotEnqueueAssetSold()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_self";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(userId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = assetId,
            AuthorId = userId,
            CategoryId = Guid.NewGuid(),
            Title = "Own",
            StorageKey = "k",
            FileName = "f.zip"
        });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // purchase completed + download ready only (no asset sold)
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedButAssetMissing_ShouldNotCreatePurchase()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_missing";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(userId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDuplicatePurchase_ShouldReturnSuccessPayloadWithoutAudit()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_dup";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(userId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = assetId,
            AuthorId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Pack",
            StorageKey = "k",
            FileName = "f.zip"
        });
        _purchaseStoreMock.Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicatePurchaseException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(userId);
        result.Value.AssetId.Should().Be(assetId);
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSameWebhookDeliveredTwice_ShouldCreatePurchaseAndAuditOnlyOnce()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        const string sessionId = "cs_same_webhook";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        Purchase? persisted = null;
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutCompleted(buyerId, assetId, sessionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>())
            .Returns(_ => persisted);
        _purchaseStoreMock.Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.Arg<Purchase>();
                return Task.FromResult(persisted!);
            });
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = Guid.NewGuid(),
            Title = "Idempotent Pack",
            StorageKey = "key",
            FileName = "pack.zip"
        });

        var first = await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        await _purchaseStoreMock.Received(1).Add(
            Arg.Is<Purchase>(purchase => purchase.StripePaymentId == sessionId),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e => e.Action == AuditActions.PAYMENT_PURCHASE_COMPLETED),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenInvalidSignature_ShouldReturnInvalidWithoutAudit()
    {
        var command = new HandleStripeWebhookCommand("bad-payload", "bad-sig");
        _paymentServiceMock.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeWebhookInvalidSignatureException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_STRIPE_WEBHOOK_INVALID);
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().WriteBestEffort(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldPropagateException()
    {
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Stripe API down"));

        var act = () => _handler.Handle(command, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Stripe API down");
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _handler.Handle(command, cts.Token));
    }
}
