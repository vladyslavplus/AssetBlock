using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class GetCheckoutStatusQueryHandlerTests
{
    private readonly ICheckoutIntentStore _checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
    private readonly IOrderStore _orderStoreMock = Substitute.For<IOrderStore>();
    private readonly GetCheckoutStatusQueryHandler _handler;

    public GetCheckoutStatusQueryHandlerTests()
    {
        _handler = new GetCheckoutStatusQueryHandler(_checkoutIntentStoreMock, _orderStoreMock);
    }

    [Fact]
    public async Task Handle_WhenIntentNotFoundOrUserMismatch_ShouldReturnNotFound()
    {
        var userId = Guid.NewGuid();
        var intentId = Guid.NewGuid();

        _checkoutIntentStoreMock.GetByIdWithItems(intentId, Arg.Any<CancellationToken>())
            .Returns((CheckoutIntent?)null);

        var query = new GetCheckoutStatusQuery(intentId, userId);
        Result<GetCheckoutStatusResponse> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenOrderExistsOrIntentCompleted_ShouldReturnCompleted()
    {
        var userId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var intent = new CheckoutIntent
        {
            Id = intentId,
            UserId = userId,
            ProductTitle = "Test Product",
            AmountTotal = 10m,
            Currency = "usd",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = CheckoutIntentStatus.PENDING
        };

        var order = new Order
        {
            Id = orderId,
            CheckoutIntentId = intentId,
            UserId = userId,
            ProductTitle = "Test Product",
            AmountPaid = 10m,
            Currency = "usd",
            StripeSessionId = "cs_test",
            PurchasedAt = DateTimeOffset.UtcNow
        };

        _checkoutIntentStoreMock.GetByIdWithItems(intentId, Arg.Any<CancellationToken>())
            .Returns(intent);
        _orderStoreMock.GetByCheckoutIntentId(intentId, Arg.Any<CancellationToken>())
            .Returns(order);

        var query = new GetCheckoutStatusQuery(intentId, userId);
        Result<GetCheckoutStatusResponse> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CheckoutFulfillmentStatuses.COMPLETED);
        result.Value.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task Handle_WhenIntentCancelled_ShouldReturnCancelled()
    {
        var userId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var intent = new CheckoutIntent
        {
            Id = intentId,
            UserId = userId,
            ProductTitle = "Test Product",
            AmountTotal = 10m,
            Currency = "usd",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = CheckoutIntentStatus.CANCELLED
        };

        _checkoutIntentStoreMock.GetByIdWithItems(intentId, Arg.Any<CancellationToken>())
            .Returns(intent);
        _orderStoreMock.GetByCheckoutIntentId(intentId, Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var query = new GetCheckoutStatusQuery(intentId, userId);
        Result<GetCheckoutStatusResponse> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CheckoutFulfillmentStatuses.CANCELLED);
        result.Value.OrderId.Should().Be(null);
    }

    [Fact]
    public async Task Handle_WhenIntentPendingAndNoOrder_ShouldReturnPending()
    {
        var userId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var intent = new CheckoutIntent
        {
            Id = intentId,
            UserId = userId,
            ProductTitle = "Test Product",
            AmountTotal = 10m,
            Currency = "usd",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = CheckoutIntentStatus.PENDING
        };

        _checkoutIntentStoreMock.GetByIdWithItems(intentId, Arg.Any<CancellationToken>())
            .Returns(intent);
        _orderStoreMock.GetByCheckoutIntentId(intentId, Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var query = new GetCheckoutStatusQuery(intentId, userId);
        Result<GetCheckoutStatusResponse> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CheckoutFulfillmentStatuses.PENDING);
        result.Value.OrderId.Should().BeNull();
    }
}
