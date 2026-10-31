using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CaseAssignment;
using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Filters;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

[ApiController]
[EnableRateLimiting(RateLimitingConstants.Policies.MODERATION_EVIDENCE)]
[Authorize(Roles = AppRoles.MODERATOR)]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
[Produces("application/json")]
[PrivateNoStore]
public sealed class ModerationSubmissionsController(ISender sender) : ControllerBase
{
    public sealed record ClaimInput(Guid OperationId, long ExpectedCaseRevision);

    [HttpGet(ApiRoutes.Moderation.QUEUE)]
    public async Task<IActionResult> Queue([FromQuery] ModerationSubmissionState? state, [FromQuery] CaseClaimFilter claimFilter = CaseClaimFilter.ALL,
        [FromQuery] int pageSize = 50, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        Ardalis.Result.Result<ModeratorQueuePage> result = await sender.Send(new GetModeratorQueueQuery(actorId, state, claimFilter, pageSize, cursor), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    [HttpPost(ApiRoutes.Moderation.CLAIM)]
    public Task<IActionResult> Claim(Guid id, [FromBody] ClaimInput input, CancellationToken cancellationToken) => ChangeClaim(id, input, true, cancellationToken);

    [HttpPost(ApiRoutes.Moderation.RELEASE)]
    public Task<IActionResult> Release(Guid id, [FromBody] ClaimInput input, CancellationToken cancellationToken) => ChangeClaim(id, input, false, cancellationToken);

    private async Task<IActionResult> ChangeClaim(Guid id, ClaimInput input, bool claim, CancellationToken ct)
    {
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        Ardalis.Result.Result<CaseClaimSaved> result = await sender.Send(new ChangeCaseClaimCommand(actorId, id, input.OperationId, input.ExpectedCaseRevision, claim), ct);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }
    [HttpGet(ApiRoutes.Moderation.SUBMISSION_BY_ID)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid moderatorUserId))
        {
            return Unauthorized();
        }

        Ardalis.Result.Result<ModerationCaseSummary> result = await sender.Send(
            new GetModerationSubmissionCaseQuery(moderatorUserId, id),
            cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }
}
