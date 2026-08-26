using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class HandleStripeWebhookCommandHandlerTests
{
    private readonly IPaymentService _paymentServiceMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly IOrderStore _orderStoreMock;
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
        var bundleStoreMock = Substitute.For<IBundleStore>();
        _orderStoreMock = Substitute.For<IOrderStore>();
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
            bundleStoreMock,
            _orderStoreMock,
            _checkoutIntentStoreMock,
            _userStoreMock,
            _unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            composer,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetVersionMissing_ShouldReturnMismatchError()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_test_missing_version";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>())
            .Returns((AssetVersion?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Select(v => v.Identifier).Should().Contain(ErrorCodes.ERR_PAYMENT_WEBHOOK_MISMATCH);
        await _orderStoreMock.DidNotReceiveWithAnyArgs()
            .CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldPersistOrderLinesAndPurchases()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        const string sessionId = "cs_test_pinned";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 12.50m, "usd"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        StubUsers(userId, sellerId);
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _orderStoreMock.Received(1).CreateWithLinesAndPurchases(
            Arg.Is<Order>(o => o.AmountPaid == 12.50m && o.Currency == "usd"),
            Arg.Is<IReadOnlyList<OrderLine>>(lines =>
                lines.Count == 1 && lines[0].AssetVersionId == versionId && lines[0].PricePaid == 12.50m),
            Arg.Is<IReadOnlyList<Purchase>>(purchases =>
                purchases.Count == 1 && purchases[0].AssetVersionId == versionId),
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
        await _orderStoreMock.DidNotReceiveWithAnyArgs()
            .CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldCreateOrderWriteAuditAndReturnPayload()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_test_session";
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        StubUsers(userId, sellerId);
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(userId);
        result.Value.AssetId.Should().Be(assetId);
        result.Value.SellerId.Should().Be(sellerId);

        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.ORDER_COMPLETED,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.PAYMENT_ORDER_COMPLETED &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceType == AuditResourceTypes.ORDER &&
                e.ActorTypeOverride == AuditActorType.USER &&
                e.ActorUserIdOverride == userId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExistingOrderBySession_ShouldReturnPayloadWithoutCreatingOrAuditing()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sessionId = "cs_existing";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        var existing = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CheckoutIntentId = Guid.NewGuid(),
            AssetId = assetId,
            ProductTitle = "Pack",
            StripeSessionId = sessionId,
            AmountPaid = 9.99m,
            Currency = "usd",
            PurchasedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = Guid.NewGuid(),
                    AssetId = assetId,
                    AssetVersionId = Guid.NewGuid(),
                    SellerId = Guid.NewGuid(),
                    Position = 1,
                    AssetTitleSnapshot = "Pack",
                    VersionNumber = 1,
                    ListPrice = 9.99m,
                    PricePaid = 9.99m,
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Personal use",
                    LicenseTerms = "terms"
                }
            ]
        };

        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => new StripeCheckoutCompleted(
                existing.CheckoutIntentId, userId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        await _orderStoreMock.DidNotReceiveWithAnyArgs()
            .CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>());
        await _unitOfWorkMock.DidNotReceiveWithAnyArgs()
            .ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedAndAssetFound_ShouldEnqueueNotificationsForBuyerAndSeller()
    {
        var buyerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_notify";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, sellerId);
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.ORDER_COMPLETED,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldEnqueueBuyerReceiptAndSellerSaleEmails()
    {
        var buyerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_email";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, sellerId);
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(2).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_RECEIPT)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_SOLD)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerIsSeller_ShouldNotEnqueueSoldNotificationOrSaleEmail()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_self";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, userId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, authorId: null);
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_RECEIPT)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_SOLD)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerMissing_ShouldStillCreateOrderAndOmitReceiptEmail()
    {
        var buyerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_missing_buyer";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        _userStoreMock.GetEmailRecipientById(buyerId, Arg.Any<CancellationToken>()).Returns((EmailRecipient?)null);
        _userStoreMock.GetEmailRecipientById(sellerId, Arg.Any<CancellationToken>())
            .Returns(new EmailRecipient(sellerId, "author@example.com"));
        StubOrderCreate();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _orderStoreMock.Received(1).CreateWithLinesAndPurchases(
            Arg.Any<Order>(),
            Arg.Any<IReadOnlyList<OrderLine>>(),
            Arg.Any<IReadOnlyList<Purchase>>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_SOLD)),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceive().Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<object>(o => IsEmail(o, EmailTemplateKind.ORDER_RECEIPT)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompletedButVersionMissing_ShouldReturnMismatchError()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_missing";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns((AssetVersion?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Select(v => v.Identifier).Should().Contain(ErrorCodes.ERR_PAYMENT_WEBHOOK_MISMATCH);
        await _orderStoreMock.DidNotReceiveWithAnyArgs()
            .CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDuplicateOrder_ShouldReadExistingOrderAndReturnSuccessPayload()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_dup_order";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        var existingOrder = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CheckoutIntentId = Guid.NewGuid(),
            AssetId = assetId,
            ProductTitle = "Pack",
            StripeSessionId = sessionId,
            AmountPaid = 9.99m,
            Currency = "usd",
            PurchasedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = Guid.NewGuid(),
                    AssetId = assetId,
                    AssetVersionId = versionId,
                    SellerId = sellerId,
                    Position = 1,
                    AssetTitleSnapshot = "Pack",
                    VersionNumber = 1,
                    ListPrice = 9.99m,
                    PricePaid = 9.99m,
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Personal use",
                    LicenseTerms = "terms"
                }
            ]
        };
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>())
            .Returns(null, existingOrder);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, sellerId);
        _orderStoreMock.CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateOrderException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(userId);
        result.Value.AssetId.Should().Be(assetId);
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDuplicateEntitlement_ShouldThrow()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_dup_entitlement";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, sellerId);
        _orderStoreMock.CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateEntitlementException());

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateEntitlementException>();
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDuplicateOrderButNoOrderFoundAfterward_ShouldThrowInvalidOperationException()
    {
        var userId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sessionId = "cs_dup_order_missing";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(userId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>()).Returns((Order?)null);
        _orderStoreMock.GetByCheckoutIntentId(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(userId, sellerId);
        _orderStoreMock.CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateOrderException());

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_WhenSameWebhookDeliveredTwice_ShouldCreateOrderAuditAndEmailOnlyOnce()
    {
        var buyerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        const string sessionId = "cs_same_webhook";
        var command = new HandleStripeWebhookCommand("payload", "sig");
        Order? persisted = null;
        var versionId = Guid.NewGuid();
        _paymentServiceMock.VerifyCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(_ => Completed(buyerId, sellerId, assetId, versionId, sessionId, 9.99m, "usd"));
        _orderStoreMock.GetByStripeSessionId(sessionId, Arg.Any<CancellationToken>())
            .Returns(_ => persisted);
        _orderStoreMock.CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.Arg<Order>();
                return Task.FromResult(persisted!);
            });
        _assetStoreMock.GetVersion(assetId, versionId, Arg.Any<CancellationToken>()).Returns(CreateVersion(assetId, versionId));
        StubUsers(buyerId, sellerId);

        var first = await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        await _orderStoreMock.Received(1).CreateWithLinesAndPurchases(
            Arg.Is<Order>(order => order.StripeSessionId == sessionId),
            Arg.Any<IReadOnlyList<OrderLine>>(),
            Arg.Any<IReadOnlyList<Purchase>>(),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e => e.Action == AuditActions.PAYMENT_ORDER_COMPLETED),
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

    private void StubOrderCreate()
    {
        _orderStoreMock.CreateWithLinesAndPurchases(
                Arg.Any<Order>(),
                Arg.Any<IReadOnlyList<OrderLine>>(),
                Arg.Any<IReadOnlyList<Purchase>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Order>()));
    }

    private StripeCheckoutCompleted Completed(
        Guid userId,
        Guid sellerId,
        Guid assetId,
        Guid assetVersionId,
        string stripeSessionId,
        decimal amount,
        string currency)
    {
        var intentId = Guid.NewGuid();
        _checkoutIntentStoreMock.GetByIdWithItems(intentId, Arg.Any<CancellationToken>())
            .Returns(new CheckoutIntent
            {
                Id = intentId,
                UserId = userId,
                AssetId = assetId,
                ProductTitle = "Pack",
                AmountTotal = amount,
                Currency = currency,
                StripeSessionId = stripeSessionId,
                Status = CheckoutIntentStatus.PENDING,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Items =
                [
                    new CheckoutIntentItem
                    {
                        Id = Guid.NewGuid(),
                        CheckoutIntentId = intentId,
                        AssetId = assetId,
                        AssetVersionId = assetVersionId,
                        SellerId = sellerId,
                        Position = 1,
                        AssetTitleSnapshot = "Pack",
                        VersionNumber = 1,
                        ListPrice = amount,
                        AllocatedPrice = amount,
                        LicenseCode = AssetLicenseCode.PERSONAL,
                        LicenseTemplateVersion = "1.0",
                        LicenseDisplayName = "Personal use",
                        LicenseTerms = "terms"
                    }
                ]
            });
        _checkoutIntentStoreMock.TryCompleteAndRelease(
                intentId, userId, stripeSessionId,
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new StripeCheckoutCompleted(intentId, userId, stripeSessionId, amount, currency);
    }

    private static bool IsEmail(object payload, EmailTemplateKind kind) =>
        payload is EmailDispatchPayload email && email.TemplateKind == kind;

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
        ReleaseNotes = "Initial release",
        LicenseCode = AssetLicenseCode.PERSONAL,
        LicenseTemplateVersion = "1.0",
        LicenseDisplayName = "Personal use",
        LicenseTerms = "terms",
        ProcessingStatus = AssetVersionProcessingStatus.READY,
        ProcessingUpdatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
