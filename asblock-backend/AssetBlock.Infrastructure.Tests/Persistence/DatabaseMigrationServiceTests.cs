using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Persistence;

public sealed class DatabaseMigrationServiceTests
{
    [Fact]
    public async Task StartAsync_withEnsureCreated_seedsCategoriesTagsAndDevAdmin_inDevelopment()
    {
        await using var db = InMemoryDbContextFactory.Create();

        var services = new ServiceCollection();
        services.AddSingleton(_ => db);
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        var provider = services.BuildServiceProvider();

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        var sut = new DatabaseMigrationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            env,
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { EnsureCreated = true, AutoMigrate = false }),
            NullLogger<DatabaseMigrationService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        db.Categories.Should().NotBeEmpty();
        db.Tags.Should().NotBeEmpty();
        db.Users.Should().Contain(u => u.Role == AppRoles.ADMIN);
    }

    [Fact]
    public async Task StartAsync_whenBothFlagsFalse_doesNothing()
    {
        await using var db = InMemoryDbContextFactory.Create();

        var services = new ServiceCollection();
        services.AddSingleton(_ => db);
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        var provider = services.BuildServiceProvider();

        var sut = new DatabaseMigrationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostEnvironment>(),
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { EnsureCreated = false, AutoMigrate = false }),
            NullLogger<DatabaseMigrationService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        db.Categories.Should().BeEmpty();
        db.Tags.Should().BeEmpty();
    }
}
