using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SECTION_NAME));
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
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

        return services;
    }
}
