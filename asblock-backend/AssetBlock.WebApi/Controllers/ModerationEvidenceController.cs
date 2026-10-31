using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.AssessCoverage;
using AssetBlock.Application.UseCases.Moderation.ListCoverageRequests;
using AssetBlock.Application.UseCases.Moderation.ReadReviewEvidence;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
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
public sealed class ModerationEvidenceController(ISender sender) : ControllerBase
{
    [HttpGet(ApiRoutes.Moderation.CASE_EVIDENCE)]
    public Task<IActionResult> CaseEvidence(Guid id, RestrictedEvidenceKind kind, [FromQuery] Guid? findingId, CancellationToken cancellationToken) => Evidence(id, false, kind, findingId, cancellationToken);
    [HttpGet(ApiRoutes.Moderation.COVERAGE_EVIDENCE)]
    public Task<IActionResult> CoverageEvidence(Guid id, RestrictedEvidenceKind kind, [FromQuery] Guid? findingId, CancellationToken cancellationToken) => Evidence(id, true, kind, findingId, cancellationToken);
    private async Task<IActionResult> Evidence(Guid id, bool coverage, RestrictedEvidenceKind kind, Guid? findingId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        return ResultProblemDetailsMapper.Map(HttpContext, await sender.Send(new ReadReviewEvidenceQuery(actorId, id, coverage, kind, findingId), ct));
    }
    [HttpGet(ApiRoutes.Moderation.COVERAGE_REQUESTS)]
    public async Task<IActionResult> List([FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        return ResultProblemDetailsMapper.Map(HttpContext, await sender.Send(new ListCoverageRequestsQuery(actorId, page), cancellationToken));
    }
    [HttpPost(ApiRoutes.Moderation.COVERAGE_ASSESSMENT)]
    public async Task<IActionResult> Assess(Guid id, [FromBody] CoverageAssessmentRequest input, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        return ResultProblemDetailsMapper.Map(HttpContext, await sender.Send(new AssessCoverageCommand(actorId, id, input), cancellationToken));
    }
}
