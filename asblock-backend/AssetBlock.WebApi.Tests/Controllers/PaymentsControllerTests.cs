using System.Text;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
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
        IOptions<StripeOptions> opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "",
                WebhookSecret = "",
                SuccessUrl = "",
                CancelUrl = ""
            });
        IActionResult result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = false });
    }

    [Fact]
    public void GetCapabilities_WhenAllPlaceholders_ShouldReturnCheckoutConfiguredFalse()
    {
        var controller = new PaymentsController(Sender);
        IOptions<StripeOptions> opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "<stripe-secret-key>",
                WebhookSecret = "<stripe-webhook-secret>",
                SuccessUrl = "<default-success-url>",
                CancelUrl = "<default-cancel-url>"
            });
        IActionResult result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = false });
    }

    [Fact]
    public void GetCapabilities_WhenFullyConfigured_ShouldReturnCheckoutConfiguredTrue()
    {
        var controller = new PaymentsController(Sender);
        IOptions<StripeOptions> opts = Options.Create(
            new StripeOptions
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                SuccessUrl = "http://localhost/checkout/success",
                CancelUrl = "http://localhost/checkout/cancel"
            });
        IActionResult result = controller.GetCapabilities(opts);
        var body = result.Should().BeOfType<OkObjectResult>().Which.Value;
        body.Should().BeEquivalentTo(new { checkoutConfigured = true });
    }

    [Fact]
    public async Task CreateCheckout_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new PaymentsController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid()), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task CreateCheckout_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<CreateCheckoutSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new CreateCheckoutSessionResponse("https://stripe.test", Guid.NewGuid()))));

        var controller = new PaymentsController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid()), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Webhook_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<OrderCompletedPayload?>(null)));

        var controller = new PaymentsController(Sender);
        var bytes = Encoding.UTF8.GetBytes("{}");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Body = new MemoryStream(bytes);
        controller.HttpContext.Request.Headers["Stripe-Signature"] = "sig";

        IActionResult result = await controller.Webhook(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Webhook_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResultError.Error<OrderCompletedPayload?>(ErrorCodes.ERR_PAYMENT_FAILED)));

        var controller = new PaymentsController(Sender);
        var bytes = "{}"u8.ToArray();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            Request = { Body = new MemoryStream(bytes), Headers =
            {
                ["Stripe-Signature"] = "sig"
            } }
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.Webhook(CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Webhook_WhenMismatchError_ShouldReturnOkWithIgnoredMismatch()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResultError.Error<OrderCompletedPayload?>(ErrorCodes.ERR_PAYMENT_WEBHOOK_MISMATCH)));

        var controller = new PaymentsController(Sender);
        var bytes = "{}"u8.ToArray();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            Request =
            {
                Body = new MemoryStream(bytes),
                Headers = { ["Stripe-Signature"] = "sig" }
            }
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.Webhook(CancellationToken.None);

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Which;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().BeEquivalentTo(new { received = true, status = "ignored_mismatch" });
    }

    [Fact]
    public async Task Webhook_WhenInvalidSignature_ShouldReturnBadRequest400()
    {
        Sender.Send(Arg.Any<HandleStripeWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResultError.Error<OrderCompletedPayload?>(ErrorCodes.ERR_STRIPE_WEBHOOK_INVALID)));

        var controller = new PaymentsController(Sender);
        var bytes = "{}"u8.ToArray();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            Request =
            {
                Body = new MemoryStream(bytes),
                Headers = { ["Stripe-Signature"] = "invalid_sig" }
            }
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.Webhook(CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status400BadRequest);
    }
}
