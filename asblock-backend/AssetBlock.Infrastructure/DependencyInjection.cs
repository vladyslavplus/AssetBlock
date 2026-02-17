using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SECTION_NAME));
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SECTION_NAME));
        services.Configure<MinioOptions>(configuration.GetSection(MinioOptions.SECTION_NAME));
        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.SECTION_NAME));
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SECTION_NAME));
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddHostedService<DatabaseMigrationService>();
        services.AddHostedService<MinioBucketEnsureHostedService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<IPurchaseStore, PurchaseStore>();
        services.AddScoped<IPaymentService, StripePaymentService>();
        services.AddScoped<IDownloadService, DownloadService>();
        services.AddScoped<IAssetStorageService, MinioAssetStorageService>();
        services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        var redisConfiguration = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConfiguration))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var opts = ConfigurationOptions.Parse(redisConfiguration);
                opts.AbortOnConnectFail = false;
                opts.ConnectTimeout = 5000;
                return ConnectionMultiplexer.Connect(opts);
            });
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

        return services;
    }
}
