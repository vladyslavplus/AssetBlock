using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace AssetBlock.WebApi.Extensions;

internal static class AuthorizationExtensions
{
    public static IServiceCollection AddAssetBlockAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, VerifiedEmailAuthorizationMiddlewareResultHandler>();
        services.AddScoped<IAuthorizationHandler, VerifiedEmailAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.VERIFIED_EMAIL, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new VerifiedEmailRequirement()));
        return services;
    }
}
