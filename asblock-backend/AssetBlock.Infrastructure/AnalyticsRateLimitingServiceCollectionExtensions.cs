using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure;

public static class AnalyticsRateLimitingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAnalyticsRateLimitingOptions(IConfiguration configuration)
        {
            services.AddOptions<AnalyticsRateLimitingOptions>()
                .Bind(configuration.GetSection(AnalyticsRateLimitingOptions.SECTION_NAME))
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<AnalyticsRateLimitingOptions>, AnalyticsRateLimitingOptionsValidator>();
            return services;
        }

        public IServiceCollection AddAnalyticsDistributedRateLimiting(IConfiguration configuration,
            IHostEnvironment environment)
        {
            services.AddAnalyticsRateLimitingOptions(configuration);
            services.TryAddSingleton(TimeProvider.System);

            var redisConfiguration = configuration.GetConnectionString("Redis");
            var redisRequired = !environment.IsDevelopment()
                                && !environment.IsEnvironment("IntegrationTesting");

            if (redisRequired && string.IsNullOrWhiteSpace(redisConfiguration))
            {
                throw new InvalidOperationException(
                    "Connection string 'Redis' is required for analytics rate limiting outside Development/IntegrationTesting.");
            }

            if (!string.IsNullOrWhiteSpace(redisConfiguration))
            {
                services.AddRedisConnectionMultiplexer(redisConfiguration);
                services.AddSingleton<IAnalyticsDistributedRateLimiter, RedisAnalyticsDistributedRateLimiter>();
            }
            else
            {
                services.AddSingleton<IAnalyticsDistributedRateLimiter, InMemoryAnalyticsDistributedRateLimiter>();
            }

            return services;
        }

        public IServiceCollection AddRedisConnectionMultiplexer(string redisConfiguration)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
            {
                return services;
            }

            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                ConfigurationOptions opts = RedisConnectionOptions.Parse(redisConfiguration);
                return ConnectionMultiplexer.Connect(opts);
            });

            return services;
        }
    }
}

internal static class RedisConnectionOptions
{
    internal static ConfigurationOptions Parse(string redisConfiguration)
    {
        var opts = ConfigurationOptions.Parse(redisConfiguration);
        opts.AbortOnConnectFail = false;
        opts.ConnectTimeout = 5000;
        return opts;
    }
}
