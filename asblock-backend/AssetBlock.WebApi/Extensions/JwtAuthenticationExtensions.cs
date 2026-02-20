using System.Text;
using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AssetBlock.WebApi.Extensions;

internal static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT signing key (Jwt:Key) is not configured.");
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];

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
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = JwtClaimTypes.ROLE
                };

                options.Events = new JwtBearerEvents
                {
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
                    OnChallenge = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        var hasAuth = ctx.Request.Headers.Authorization.Count > 0;
                        logger.LogInformation(
                            "JWT challenge: {Path}, HasAuthorizationHeader={HasAuth}, Error={Error}",
                            ctx.Request.Path,
                            hasAuth,
                            ctx.AuthenticateFailure?.Message ?? (hasAuth ? "invalid or expired token" : "missing Bearer token"));
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }
}
