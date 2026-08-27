using System.Text;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AssetBlock.WebApi.Extensions;

internal static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SECTION_NAME).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");
        if (string.IsNullOrWhiteSpace(jwt.Key))
        {
            throw new InvalidOperationException("JWT signing key (Jwt:Key) is not configured.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep "role" as "role", not mapped to long SOAP URI
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireSignedTokens = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = JwtClaimTypes.ROLE
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        // WebSockets cannot set Authorization; SignalR clients pass access_token in the query string.
                        var accessToken = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) &&
                            ctx.Request.Path.StartsWithSegments(ApiRoutes.Hubs.NOTIFICATIONS))
                        {
                            ctx.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        var reason = ResolveJwtFailureReason(ctx.Exception);
                        logger.LogDebug("JWT authentication failed: {Reason}", reason);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        var sub = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        logger.LogDebug("JWT validated for subject {Subject}", sub);
                        return Task.CompletedTask;
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();

                        // If authentication failed (e.g. expired or bad signature token), OnAuthenticationFailed already logged the reason.
                        // Only log challenge here when AuthenticateFailure is null (e.g. missing token).
                        if (ctx.AuthenticateFailure is null)
                        {
                            var hasAuth = ctx.Request.Headers.Authorization.Count > 0;
                            logger.LogDebug(
                                "JWT challenge: {Path}, HasAuthorizationHeader={HasAuth}, Reason=missing_token",
                                ctx.Request.Path,
                                hasAuth);
                        }

                        var problem = AssetBlockProblemDetails.Create(
                            ctx.HttpContext,
                            StatusCodes.Status401Unauthorized,
                            ErrorCodes.ERR_AUTH_TOKEN_INVALID);
                        await AssetBlockProblemDetails.Write(ctx.HttpContext, problem);
                    },
                    OnForbidden = async ctx =>
                    {
                        var problem = AssetBlockProblemDetails.Create(
                            ctx.HttpContext,
                            StatusCodes.Status403Forbidden,
                            ErrorCodes.ERR_FORBIDDEN);
                        await AssetBlockProblemDetails.Write(ctx.HttpContext, problem);
                    }
                };
            });
        return services;
    }

    private static string ResolveJwtFailureReason(Exception? exception)
    {
        return exception switch
        {
            SecurityTokenExpiredException => "expired",
            SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => "bad_signature",
            SecurityTokenInvalidAudienceException => "bad_audience",
            SecurityTokenInvalidIssuerException => "bad_issuer",
            SecurityTokenNotYetValidException => "not_yet_valid",
            SecurityTokenMalformedException or ArgumentException => "malformed",
            _ => "invalid"
        };
    }
}
