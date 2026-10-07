using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Assets.WithdrawSubmission;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>Owner-side withdrawal of an active moderation submission.</summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
[Produces("application/json")]
public sealed class SellerSubmissionsController(ISender sender) : ControllerBase
{
    [HttpPost(ApiRoutes.Moderation.SUBMISSION_WITHDRAW)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] SubmissionWithdrawRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Ardalis.Result.Result<WithdrawSubmissionResult> result = await sender.Send(
            new WithdrawSubmissionCommand(userId, id, request.ExpectedCaseRevision, request.OperationId),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ResultProblemDetailsMapper.Map(HttpContext, result);
    }
}
