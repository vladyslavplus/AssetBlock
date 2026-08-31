using System.Security.Claims;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.WebApi.Extensions;

/// <summary>
/// Resolves the authenticated user id from the principal. <see cref="ClaimTypes.NameIdentifier"/> takes
/// precedence; <c>sub</c> is the fallback so the read path matches the authorization handler that
/// mints/consumes internal access tokens. Resolution is independent of authentication state — endpoint
/// authorization remains the responsibility of <c>[Authorize]</c>.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal principal)
    {
        public bool TryGetUserId(out Guid userId)
        {
            var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtClaimTypes.SUB);
            return Guid.TryParse(value, out userId);
        }

        public Guid? GetUserIdOrNull() =>
            principal.TryGetUserId(out Guid userId) ? userId : null;
    }
}
