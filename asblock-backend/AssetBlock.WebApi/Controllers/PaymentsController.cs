using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.WebApi.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AssetBlock.WebApi.Controllers;

public sealed class PaymentsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Whether Stripe checkout can be used (keys and default redirect URLs are set). For future storefront UI; does not call Stripe.
    /// </summary>
    [HttpGet(ApiRoutes.Payments.CAPABILITIES)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCapabilities([FromServices] IOptions<StripeOptions> stripeOptions)
    {
        var o = stripeOptions.Value;
        var checkoutConfigured =
            !string.IsNullOrWhiteSpace(o.SecretKey)
            && !string.IsNullOrWhiteSpace(o.WebhookSecret)
            && !string.IsNullOrWhiteSpace(o.DefaultSuccessUrl)
            && !string.IsNullOrWhiteSpace(o.DefaultCancelUrl);
        return Ok(new { checkoutConfigured });
    }

    /// <summary>
    /// Create a Stripe Checkout session for an asset. Returns redirect URL.
    /// </summary>
    [HttpPost(ApiRoutes.Payments.CHECKOUT)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateCheckoutRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new CreateCheckoutSessionCommand(request.AssetId, userId.Value, request.SuccessUrl, request.CancelUrl);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Stripe webhook. Do not call directly; Stripe calls this on checkout.session.completed.
    /// </summary>
    [HttpPost(ApiRoutes.Payments.WEBHOOK)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault() ?? string.Empty;
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        var command = new HandleStripeWebhookCommand(payload, signature);
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }
}
