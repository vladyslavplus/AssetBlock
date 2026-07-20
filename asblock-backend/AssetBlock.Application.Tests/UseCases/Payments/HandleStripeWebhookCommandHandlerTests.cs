using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
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
    private readonly ICheckoutIntentStore _checkoutIntentStoreMock;
    private readonly IUserStore _userStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly HandleStripeWebhookCommandHandler _handler;

    public HandleStripeWebhookCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        _userStoreMock = Substitute.For<IUserStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        var composer = new TransactionalEmailComposer(Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
        }));

        _handler = new HandleStripeWebhookCommandHandler(
            _paymentServiceMock,
            _assetStoreMock,
            _purchaseStoreMock,
            _checkoutIntentStoreMock,
            _userStoreMock,
            _unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            composer,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetVersionMissing_ShouldRejectWebhookForRetry()
    {
        var userId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_test_missing_version";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>())
            .Returns((AssetVersion?)null);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing asset version*");
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldPersistPinnedVersionPriceAndCurrency()
    {
        var userId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_test_pinned";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 12.50m, "eur"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        StubUsers(userId, authorId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _purchaseStoreMock.Received(1).Add(
            Arg.Is<Purchase>(p =>
                p.AssetVersionId == versionId &&
                p.PricePaid == 12.50m &&
                p.Currency == "eur"),
            Arg.Any<CancellationToken>());
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
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_test_session";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>())
            .Returns(new AssetVersion
            {
                Id = versionId, AssetId = assetId, VersionNumber = 1, IsCurrent = true,
                StorageKey = "k", FileName = "f.zip", ContentLength = 1, ContentSha256 = new string('a', 64),
                LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal use", LicenseTerms = "terms"
            });
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        StubUsers(userId, authorId);

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
            .Returns(_ => Completed(userId, assetId, Guid.NewGuid(), sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            AssetVersionId = Guid.NewGuid(),
            CheckoutIntentId = Guid.NewGuid(),
            PricePaid = 9.99m,
            Currency = "usd",
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
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedAndAssetFound_ShouldEnqueueNotificationsForBuyerAndAuthor()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_notify";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, authorId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.PURCHASE_COMPLETED,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(3).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldEnqueueBuyerReceiptAndAuthorSaleEmails()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_email";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, authorId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.PURCHASE_RECEIPT)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ASSET_SOLD)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerIsAuthor_ShouldNotEnqueueAssetSoldOrSaleEmail()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_self";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, userId, "Own"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, authorId: null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.PURCHASE_RECEIPT)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ASSET_SOLD)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerMissing_ShouldStillCreatePurchaseAndOmitReceiptEmail()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_missing_buyer";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        _userStoreMock.GetEmailRecipientById(buyerId, Arg.Any<CancellationToken>()).Returns((EmailRecipient?)null);
        _userStoreMock.GetEmailRecipientById(authorId, Arg.Any<CancellationToken>())
            .Returns(new EmailRecipient(authorId, "author@example.com"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _purchaseStoreMock.Received(1).Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ASSET_SOLD)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.PURCHASE_RECEIPT)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedButAssetMissing_ShouldRejectWebhookForRetry()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_missing";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns((AssetVersion?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing asset*");
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDuplicatePurchase_ShouldReturnSuccessPayloadWithoutAudit()
    {
        var userId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_dup";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, authorId);
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
    public async Task Handle_WhenSameWebhookDeliveredTwice_ShouldCreatePurchaseAuditAndEmailOnlyOnce()
    {
        var buyerId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        const string sessionId = "cs_same_webhook";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        Purchase? persisted = null;
        var versionId = Guid.NewGuid();
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _purchaseStoreMock.GetByStripePaymentId(sessionId, Arg.Any<CancellationToken>())
            .Returns(_ => persisted);
        _purchaseStoreMock.Add(Arg.Any<Purchase>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.Arg<Purchase>();
                return Task.FromResult(persisted!);
            });
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(CreateAsset(assetId, authorId, "Idempotent Pack"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, authorId);

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
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Any<object>(),
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

    private void StubUsers(Guid buyerId, Guid? authorId)
    {
        _userStoreMock.GetEmailRecipientById(buyerId, Arg.Any<CancellationToken>())
            .Returns(new EmailRecipient(buyerId, "buyer@example.com"));
        if (authorId is { } id)
        {
            _userStoreMock.GetEmailRecipientById(id, Arg.Any<CancellationToken>())
                .Returns(new EmailRecipient(id, "author@example.com"));
        }
    }

    private StripeCheckoutCompleted Completed(
        Guid userId,
        Guid assetId,
        Guid assetVersionId,
        string stripeSessionId,
        decimal amount,
        string currency)
    {
        var intentId = Guid.NewGuid();
        _checkoutIntentStoreMock.GetById(intentId, Arg.Any<CancellationToken>())
            .Returns(new CheckoutIntent
            {
                Id = intentId,
                UserId = userId,
                AssetId = assetId,
                AssetVersionId = assetVersionId,
                AssetTitle = "Pack",
                UnitAmount = amount,
                Currency = currency,
                StripeSessionId = stripeSessionId,
                Status = CheckoutIntentStatus.PENDING,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        _checkoutIntentStoreMock.TryComplete(
                intentId, userId, assetId, assetVersionId, stripeSessionId,
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new StripeCheckoutCompleted(intentId, userId, assetId, assetVersionId, stripeSessionId, amount, currency);
    }

    private static bool IsEmail(object payload, EmailTemplateKind kind) =>
        payload is EmailDispatchPayload email && email.TemplateKind == kind;

    private static Asset CreateAsset(Guid assetId, Guid authorId, string title) => new()
    {
        Id = assetId,
        AuthorId = authorId,
        CategoryId = Guid.NewGuid(),
        Title = title,
        StorageKey = "k",
        FileName = "f.zip"
    };

    private static AssetVersion CreateVersion(Guid assetId, Guid versionId) => new()
    {
        Id = versionId,
        AssetId = assetId,
        VersionNumber = 1,
        IsCurrent = true,
        StorageKey = "k",
        FileName = "f.zip",
        ContentLength = 100,
        ContentSha256 = new string('a', 64),
        LicenseCode = AssetLicenseCode.PERSONAL,
        LicenseTemplateVersion = "1.0",
        LicenseDisplayName = "Personal use",
        LicenseTerms = "terms"
    };
}
