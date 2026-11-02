using System.Text.Json.Serialization;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CaseAssignment;
using AssetBlock.Application.UseCases.Moderation.DecideModerationCase;
using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Filters;
using AssetBlock.WebApi.Json;
using AssetBlock.WebApi.ProblemDetails;
using FluentValidation;
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
    /// <summary>Decision bodies carry at most 200 dispositions with bounded text; anything larger is refused before binding.</summary>
    private const int DECISION_BODY_LIMIT_BYTES = 1_048_576;
    private static readonly HashSet<string> _decisionBlockerCodes =
    [
        ErrorCodes.ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED,
        ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE,
        ErrorCodes.ERR_REVIEW_COVERAGE_REQUIRED,
        ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED,
        ErrorCodes.ERR_MODERATION_WORKSPACE_STALE,
        ErrorCodes.ERR_DECLARATION_INCOMPLETE,
        ErrorCodes.ERR_COVERAGE_ASSESSMENT_INVALIDATED,
        ErrorCodes.ERR_COVERAGE_ASSESSOR_UNAVAILABLE,
        ErrorCodes.ERR_COVERAGE_ASSESSMENT_BLOCKED,
        ErrorCodes.ERR_REVIEW_PLAN_REFERENCE_MISMATCH,
        ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE,
        ErrorCodes.ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING,
        ErrorCodes.ERR_MODERATION_DISPOSITIONS_INVALID,
        ErrorCodes.ERR_MODERATION_DECISION_BLOCKED,
        ErrorCodes.ERR_PUBLICATION_METADATA_UNAVAILABLE,
        ErrorCodes.ERR_MODERATION_CONCURRENT_UPDATE
    ];

    public sealed record ClaimInput(Guid OperationId, long ExpectedCaseRevision);

    /// <summary>
    /// Decision request body. Every required member is a <c>required</c> property, so an absent member fails to
    /// deserialize, and the enum members use the string-only converter, so numeric tokens fail too.
    /// </summary>
    public sealed class DecisionInput
    {
        public required Guid OperationId { get; init; }
        public required DecisionBasisInput Basis { get; init; }
        [JsonConverter(typeof(StrictJsonStringEnumConverter))]
        public required ModerationDecisionOutcome? Outcome { get; init; }
        public required string Rationale { get; init; }
        public required IReadOnlyList<DecisionDispositionInput> Dispositions { get; init; }
        public string? ManualPlanCompletionReference { get; init; }
    }

    public sealed class DecisionBasisInput
    {
        public required Guid VersionId { get; init; }
        public required string ContentSha256 { get; init; }
        public required Guid AnalysisReportId { get; init; }
        public required int DeclarationRevision { get; init; }
        public required int MaterialMetadataRevision { get; init; }
        public required string PolicyVersion { get; init; }
        public required int SellerEvidenceRevision { get; init; }
        public required long CaseRevision { get; init; }
    }

    public sealed class DecisionDispositionInput
    {
        public required Guid FindingId { get; init; }
        [JsonConverter(typeof(StrictJsonStringEnumConverter))]
        public required FindingDispositionType? Disposition { get; init; }
        public required string Rationale { get; init; }
    }

    public sealed class DecisionInputValidator : AbstractValidator<DecisionInput>
    {
        public DecisionInputValidator()
        {
            RuleFor(x => x.Outcome).NotNull();
            RuleForEach(x => x.Dispositions).NotNull().ChildRules(d =>
                d.RuleFor(x => x.Disposition).NotNull());
        }
    }

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

    [HttpPost(ApiRoutes.Moderation.DECISION)]
    [RequestSizeLimit(DECISION_BODY_LIMIT_BYTES)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecisionInput input, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid actorId))
        { return Unauthorized(); }
        FluentValidation.Results.ValidationResult validation = new DecisionInputValidator().Validate(input);
        if (!validation.IsValid)
        {
            foreach (FluentValidation.Results.ValidationFailure error in validation.Errors)
            { ModelState.AddModelError(error.PropertyName, error.ErrorMessage); }
            return ValidationProblem(ModelState);
        }
        Ardalis.Result.Result<ModerationDecisionSaved> result = await sender.Send(
            new DecideModerationCaseCommand(actorId, id, input.OperationId,
                input.Basis is null ? null : new DecisionBasis(input.Basis.VersionId, input.Basis.ContentSha256, input.Basis.AnalysisReportId,
                    input.Basis.DeclarationRevision, input.Basis.MaterialMetadataRevision, input.Basis.PolicyVersion,
                    input.Basis.SellerEvidenceRevision, input.Basis.CaseRevision),
                input.Outcome!.Value, input.Rationale,
                input.Dispositions?.Select(d => d is null ? null : new FindingDispositionInput(d.FindingId, d.Disposition!.Value, d.Rationale)).ToArray(),
                input.ManualPlanCompletionReference),
            cancellationToken);
        if (result.Status == Ardalis.Result.ResultStatus.Conflict
            && _decisionBlockerCodes.Contains(result.Errors.FirstOrDefault() ?? string.Empty))
        {
            var code = result.Errors.First();
            Microsoft.AspNetCore.Mvc.ProblemDetails problem = AssetBlockProblemDetails.Create(HttpContext, StatusCodes.Status409Conflict, code);
            problem.Extensions["blockerCodes"] = new[] { code };
            return AssetBlockProblemDetails.ToActionResult(problem);
        }
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
