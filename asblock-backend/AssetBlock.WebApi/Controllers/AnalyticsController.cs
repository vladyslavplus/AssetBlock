using AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Public engagement telemetry ingestion.
/// Absolute route avoids inheriting api/[controller] from ApiControllerBase.
/// </summary>
[Route(ApiRoutes.Analytics.BASE)]
public sealed class AnalyticsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Records one engagement beacon. Authentication is optional and only used to attribute the event
    /// to an actor and to suppress a seller's own activity. A malformed envelope is rejected; any
    /// well-formed envelope is accepted with 202 whether or not a row was written, so the response
    /// reveals nothing about catalog contents or download entitlements.
    /// </summary>
    [HttpPost(ApiRoutes.Analytics.EVENTS)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.ANALYTICS_EVENTS)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> IngestEvent(
        [FromBody] IngestAnalyticsEventRequest request,
        CancellationToken cancellationToken)
    {
        var command = new IngestAnalyticsEventCommand(request, GetUserId());
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Accepted() : MapResultToActionResult(result);
    }
}
