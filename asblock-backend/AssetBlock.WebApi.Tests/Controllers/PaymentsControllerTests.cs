using System.Text;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class PaymentsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    public void GetCapabilities_WhenStripeKeysMissing_ShouldReturnCheckoutConfiguredFalse()
    {
        var controller = new PaymentsController(Sender);
        var opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "",
                WebhookSecret = "",
                DefaultSuccessUrl = "",
                DefaultCancelUrl = ""
            });
        var result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = false });
    }

    [Fact]
    public void GetCapabilities_WhenAllPlaceholders_ShouldReturnCheckoutConfiguredFalse()
    {
        var controller = new PaymentsController(Sender);
        var opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "<stripe-secret-key>",
                WebhookSecret = "<stripe-webhook-secret>",
                DefaultSuccessUrl = "<default-success-url>",
                DefaultCancelUrl = "<default-cancel-url>"
            });
        var result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = false });
    }

    [Fact]
    public void GetCapabilities_WhenFullyConfigured_ShouldReturnCheckoutConfiguredTrue()
    {
        var controller = new PaymentsController(Sender);
        var opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost/success",
                DefaultCancelUrl = "http://localhost/cancel"
            });
        var result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = true });
    }

    [Fact]
    public async Task CreateCheckout_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new PaymentsController(Sender);
        SetupAnonymous(controller);
        var result = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid()), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
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
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.Headers["Stripe-Signature"] = "sig";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.Webhook(CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status400BadRequest);
    }
}
