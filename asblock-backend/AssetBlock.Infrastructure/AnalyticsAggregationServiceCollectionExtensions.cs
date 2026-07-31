using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure;

public static class AnalyticsAggregationServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsAggregationOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AnalyticsAggregationOptions>()
            .Bind(configuration.GetSection(AnalyticsAggregationOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnalyticsAggregationOptions>, AnalyticsAggregationOptionsValidator>();
        return services;
    }
}
