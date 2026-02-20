using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Payments;

public class HandleStripeWebhookCommandHandlerTests
{
    private readonly IPaymentService _paymentServiceMock;
    private readonly HandleStripeWebhookCommandHandler _handler;

    public HandleStripeWebhookCommandHandlerTests()
    {
        _paymentServiceMock = Substitute.For<IPaymentService>();
        _handler = new HandleStripeWebhookCommandHandler(
            _paymentServiceMock,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceReturnsNull_ShouldReturnSuccessWithNullPayload()
    {
        // Arrange — event is not a checkout.completed (e.g., another event type)
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.HandleCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns((( Guid, Guid)?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenCheckoutCompleted_ShouldReturnCorrectPurchasePayload()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var command = new HandleStripeWebhookCommand("payload", "sig");

        _paymentServiceMock.HandleCheckoutCompleted(command.Payload, command.Signature, Arg.Any<CancellationToken>())
            .Returns(((Guid, Guid)?)(userId, assetId));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(userId);
        result.Value.AssetId.Should().Be(assetId);
    }

    [Fact]
    public async Task Handle_WhenPaymentServiceThrows_ShouldReturnErrorResult()
    {
        // Arrange — simulates Stripe signature mismatch or network failure
        var command = new HandleStripeWebhookCommand("bad-payload", "bad-sig");
        _paymentServiceMock.HandleCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Stripe signature validation failed"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Error);
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldPropagateCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var command = new HandleStripeWebhookCommand("payload", "sig");
        _paymentServiceMock.HandleCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _handler.Handle(command, cts.Token));
    }
}
