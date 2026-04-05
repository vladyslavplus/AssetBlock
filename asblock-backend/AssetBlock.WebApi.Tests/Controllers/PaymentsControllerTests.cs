using System.Text;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class PaymentsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    public async Task CreateCheckout_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new PaymentsController(Sender);
        SetupAnonymous(controller);
        var result = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task CreateCheckout_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<CreateCheckoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new CreateCheckoutSessionResponse("https://stripe.test"))));

        var controller = new PaymentsController(Sender);
        SetupUser(_userId, controller);
        var action = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid()), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Webhook_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<PurchaseCompletedPayload?>(null)));

        var controller = new PaymentsController(Sender);
        var bytes = Encoding.UTF8.GetBytes("{}");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Body = new MemoryStream(bytes);
        controller.HttpContext.Request.Headers["Stripe-Signature"] = "sig";

        var result = await controller.Webhook(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Webhook_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResultError.Error<PurchaseCompletedPayload?>(ErrorCodes.ERR_PAYMENT_FAILED)));

        var controller = new PaymentsController(Sender);
        var bytes = Encoding.UTF8.GetBytes("{}");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Body = new MemoryStream(bytes);
        controller.HttpContext.Request.Headers["Stripe-Signature"] = "sig";

        var result = await controller.Webhook(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
