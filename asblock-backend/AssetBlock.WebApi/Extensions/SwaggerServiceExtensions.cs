using Microsoft.OpenApi;

namespace AssetBlock.WebApi.Extensions;

internal static class SwaggerServiceExtensions
{
    public static IServiceCollection AddSwaggerConfiguration(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "AssetBlock API",
                Version = "v1",
                Description = "Marketplace API for developers: assets, secure purchase, and encrypted delivery. " +
                              "Use the Authorize button to set a JWT Bearer token for protected endpoints."
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                Description = "Enter JWT token only (e.g. eyJhbG...). The 'Bearer ' prefix is added automatically by Swagger."
            });

            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        });

        return services;
    }
}
