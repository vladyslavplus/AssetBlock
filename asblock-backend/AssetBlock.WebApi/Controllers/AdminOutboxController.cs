using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;
using AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Admin-only management endpoints for the transactional outbox dead-letter state.
/// </summary>
[ApiController]
[Authorize(Roles = AppRoles.ADMIN)]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
[Produces("application/json")]
public sealed class AdminOutboxController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Gets a paged list of dead-lettered outbox messages without serialized payloads.
    /// Ordered by DeadLetteredAt DESC, OccurredAt DESC, Id ASC.
    /// </summary>
    [HttpGet(ApiRoutes.Admin.DEAD_LETTERS)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] GetDeadLettersRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new GetDeadLettersRequest();
        Result<Domain.Core.Dto.Paging.PagedResult<DeadLetterOutboxListItemDto>> result = await sender.Send(new GetDeadLettersQuery(request), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Replays a dead-lettered outbox message by resetting its status to pending.
    /// </summary>
    [HttpPost(ApiRoutes.Admin.DEAD_LETTER_REPLAY)]
    [EnableRateLimiting(RateLimitingConstants.Policies.ADMIN_OUTBOX_REPLAY)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Replay(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        Result<ReplayDeadLetterResponseDto> result = await sender.Send(new ReplayDeadLetterCommand(id), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }
}
