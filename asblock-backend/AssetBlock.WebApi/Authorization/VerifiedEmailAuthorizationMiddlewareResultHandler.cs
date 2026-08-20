using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AssetBlock.WebApi.Authorization;

/// <summary>
/// Preserves default challenge/forbid for all policies except VERIFIED_EMAIL,
/// which returns the standard AssetBlock problem+json with ERR_EMAIL_NOT_VERIFIED.
/// </summary>
internal sealed class VerifiedEmailAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult is { Forbidden: true, AuthorizationFailure: { } failure }
            && failure.FailedRequirements.OfType<VerifiedEmailRequirement>().Any()
            && !failure.FailedRequirements.OfType<RolesAuthorizationRequirement>().Any())
        {
            var problem = AssetBlockProblemDetails.Create(
                context,
                StatusCodes.Status403Forbidden,
                ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
            await AssetBlockProblemDetails.Write(context, problem);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
