using System.Security.Claims;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.Authorization;

namespace AssetBlock.WebApi.Authorization;

internal sealed class VerifiedEmailAuthorizationHandler(IUserVerificationStore verificationStore)
    : AuthorizationHandler<VerifiedEmailRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VerifiedEmailRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var subject =
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue(JwtClaimTypes.SUB);
        if (!Guid.TryParse(subject, out var userId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;

        if (await verificationStore.IsEmailVerified(userId, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}
