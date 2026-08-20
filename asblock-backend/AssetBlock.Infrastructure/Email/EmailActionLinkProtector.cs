using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Email;

internal sealed class EmailActionLinkProtector(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EmailOptions> emailOptions,
    ILogger<EmailActionLinkProtector> logger) : IEmailActionLinkProtector
{
    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider
        .CreateProtector(EmailActionConstants.DATA_PROTECTION_PURPOSE)
        .ToTimeLimitedDataProtector();

    public string Protect(EmailActionLinkClaims claims)
    {
        var lifetime = claims.ExpiresAt - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Email action link is already expired.");
        }

        var payload = Encode(claims);
        var protectedBytes = _protector.Protect(payload, claims.ExpiresAt);
        return WebEncoders.Base64UrlEncode(protectedBytes);
    }

    public bool TryUnprotect(string protectedToken, EmailActionPurpose expectedPurpose, out EmailActionLinkClaims claims)
    {
        claims = null!;
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return false;
        }

        if (protectedToken.Length > EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH)
        {
            return false;
        }

        try
        {
            var protectedBytes = WebEncoders.Base64UrlDecode(protectedToken.Trim());
            var bytes = _protector.Unprotect(protectedBytes, out _);
            if (!TryDecode(bytes, out var decoded) || decoded.Purpose != expectedPurpose)
            {
                return false;
            }

            if (decoded.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            claims = decoded;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Email action token unprotect failed with {ExceptionType}",
                ex.GetType().Name);
            return false;
        }
    }

    public string BuildActionUrl(EmailActionPurpose purpose, string protectedToken)
    {
        var path = purpose switch
        {
            EmailActionPurpose.EMAIL_VERIFICATION => "/verify-email",
            EmailActionPurpose.PASSWORD_RESET => "/reset-password",
            EmailActionPurpose.EMAIL_CHANGE => "/confirm-email-change",
            _ => throw new InvalidOperationException("Unsupported email action purpose.")
        };

        var origin = GetOrigin();
        // Fragment (#token=) is not sent to servers/proxies/logs on navigation — safer than query string.
        var encoded = Uri.EscapeDataString(protectedToken);
        return $"{origin}{path}#token={encoded}";
    }

    private string GetOrigin()
    {
        var configured = emailOptions.Value.PublicAppBaseUrl.Trim();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Email:PublicAppBaseUrl must be a configured absolute http(s) origin.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static byte[] Encode(EmailActionLinkClaims claims)
    {
        // Fixed binary layout: actionId(16) + version(16) + purpose(4) + expiresUnixMs(8)
        var buffer = new byte[44];
        claims.ActionId.TryWriteBytes(buffer.AsSpan(0, 16));
        claims.Version.TryWriteBytes(buffer.AsSpan(16, 16));
        BitConverter.TryWriteBytes(buffer.AsSpan(32, 4), (int)claims.Purpose);
        BitConverter.TryWriteBytes(buffer.AsSpan(36, 8), claims.ExpiresAt.ToUnixTimeMilliseconds());
        return buffer;
    }

    private static bool TryDecode(byte[] bytes, out EmailActionLinkClaims claims)
    {
        claims = null!;
        if (bytes.Length != 44)
        {
            return false;
        }

        var actionId = new Guid(bytes.AsSpan(0, 16));
        var version = new Guid(bytes.AsSpan(16, 16));
        var purposeValue = BitConverter.ToInt32(bytes.AsSpan(32, 4));
        if (!Enum.IsDefined(typeof(EmailActionPurpose), purposeValue))
        {
            return false;
        }

        var expiresMs = BitConverter.ToInt64(bytes.AsSpan(36, 8));
        claims = new EmailActionLinkClaims(
            actionId,
            version,
            (EmailActionPurpose)purposeValue,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresMs));
        return true;
    }
}
