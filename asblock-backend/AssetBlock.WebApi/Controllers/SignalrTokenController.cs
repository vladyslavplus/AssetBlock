using System.Security.Claims;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Issues short-lived hub-only JWTs for SignalR WebSocket authentication.
/// The hub token has a distinct audience and <c>token_use=signalr</c> claim so it cannot
/// authorise any REST endpoint protected by the default API bearer scheme.
/// </summary>
[ApiController]
[Route(ApiRoutes.Auth.BASE)]
[Produces("application/json")]
public sealed class SignalrTokenController(IJwtTokenService jwtTokenService) : ControllerBase
{
    /// <summary>
    /// Returns a short-lived hub-only JWT. Requires a valid session (API bearer token).
    /// </summary>
    [HttpPost(ApiRoutes.Auth.SIGNALR_TOKEN)]
    [Authorize(AuthenticationSchemes = JwtAuthenticationSchemes.API)]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_SIGNALR_TOKEN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetHubToken()
    {
        var userId = GetUserId();
        if (userId is null)
        {
            var problem = AssetBlockProblemDetails.Create(
                HttpContext,
                StatusCodes.Status401Unauthorized,
                ErrorCodes.ERR_AUTH_TOKEN_INVALID);
            return AssetBlockProblemDetails.ToActionResult(problem);
        }

        var response = jwtTokenService.GenerateHubToken(userId.Value);
        return Ok(new { hubToken = response.HubToken, expiresAt = response.ExpiresAt });
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtClaimTypes.SUB);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
