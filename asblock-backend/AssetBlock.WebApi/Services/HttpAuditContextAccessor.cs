using System.Security.Claims;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.WebApi.Services;

internal sealed class HttpAuditContextAccessor(IHttpContextAccessor httpContextAccessor) : IAuditContextAccessor
{
    public CurrentAuditContext? GetCurrent()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        var user = httpContext.User;
        Guid? actorUserId = null;
        var actorType = AuditActorType.ANONYMOUS;

        if (user.Identity?.IsAuthenticated == true)
        {
            var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue("sub");
            if (Guid.TryParse(sub, out var parsed))
            {
                actorUserId = parsed;
                actorType = AuditActorType.USER;
            }
        }

        var ip = httpContext.Connection.RemoteIpAddress switch
        {
            null => null,
            { IsIPv4MappedToIPv6: true } address => address.MapToIPv4().ToString(),
            var address => address.ToString()
        };

        var userAgent = httpContext.Request.Headers.UserAgent.FirstOrDefault();

        return new CurrentAuditContext(
            actorType,
            actorUserId,
            Truncate(Sanitize(httpContext.TraceIdentifier), AuditFieldLimits.TRACE_ID_MAX_LENGTH),
            Truncate(Sanitize(ip), AuditFieldLimits.IP_ADDRESS_MAX_LENGTH),
            Truncate(Sanitize(userAgent), AuditFieldLimits.USER_AGENT_MAX_LENGTH));
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
