using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

[ApiController]
[Authorize(Roles = AppRoles.MODERATOR)]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
[Produces("application/json")]
public sealed class ModerationSubmissionsController(ISender sender) : ControllerBase
{
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
