using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Hubs;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Controllers;

public sealed class PaymentsController(ISender sender, IHubContext<NotificationsHub> hubContext) : ApiControllerBase(sender)
{
    /// <summary>
    /// Create a Stripe Checkout session for an asset. Returns redirect URL.
    /// </summary>
    [HttpPost(ApiRoutes.Payments.CHECKOUT)]
    [Authorize]
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
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault() ?? string.Empty;
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        var command = new HandleStripeWebhookCommand(payload, signature);
        var result = await Sender.Send(command, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return Ok();
        }

        var completed = result.Value;
        await hubContext.Clients.User(completed.UserId.ToString())
            .SendAsync(NotificationsHub.PURCHASE_COMPLETED, completed.AssetId, cancellationToken);

        return Ok();
    }
}
