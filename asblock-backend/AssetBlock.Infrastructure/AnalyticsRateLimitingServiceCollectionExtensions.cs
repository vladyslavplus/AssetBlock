using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure;

public static class AnalyticsRateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsRateLimitingOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AnalyticsRateLimitingOptions>()
            .Bind(configuration.GetSection(AnalyticsRateLimitingOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnalyticsRateLimitingOptions>, AnalyticsRateLimitingOptionsValidator>();
        return services;
    }
}
