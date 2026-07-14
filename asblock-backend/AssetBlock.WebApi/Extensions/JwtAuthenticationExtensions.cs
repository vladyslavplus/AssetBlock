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
                        logger.LogWarning(ctx.Exception, "JWT validation failed: {Reason}", ctx.Exception.Message);
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
                        var hasAuth = ctx.Request.Headers.Authorization.Count > 0;
                        logger.LogInformation(
                            "JWT challenge: {Path}, HasAuthorizationHeader={HasAuth}, Error={Error}",
                            ctx.Request.Path,
                            hasAuth,
                            ctx.AuthenticateFailure?.Message ?? (hasAuth ? "invalid or expired token" : "missing Bearer token"));

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
        services.AddAuthorization();
        return services;
    }
}
