using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class HostFilteringExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("localhost;127.0.0.1")]
    public void AddAssetBlockHostFiltering_WhenDevelopment_ShouldAllowAnyAllowedHosts(string? allowedHosts)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(allowedHosts);
        var env = CreateEnvironment(Environments.Development);

        var act = () => services.AddAssetBlockHostFiltering(config, env);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public void AddAssetBlockHostFiltering_WhenIntegrationTesting_ShouldAllowMissingOrWildcard(string? allowedHosts)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(allowedHosts);
        var env = CreateEnvironment("IntegrationTesting");

        var act = () => services.AddAssetBlockHostFiltering(config, env);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAssetBlockHostFiltering_WhenProductionAndMissingOrEmpty_ShouldThrow(string? allowedHosts)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(allowedHosts);
        var env = CreateEnvironment(Environments.Production);

        var act = () => services.AddAssetBlockHostFiltering(config, env);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'AllowedHosts' must be explicitly configured*");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.assetblock.com")]
    [InlineData("api.*.assetblock.com")]
    [InlineData("assetblock.*")]
    [InlineData("api.assetblock.com;*")]
    [InlineData("api.assetblock.com; *.assetblock.com")]
    [InlineData("api.assetblock.com; * ; assetblock.com")]
    public void AddAssetBlockHostFiltering_WhenProductionAndContainsWildcard_ShouldThrow(string allowedHosts)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(allowedHosts);
        var env = CreateEnvironment(Environments.Production);

        var act = () => services.AddAssetBlockHostFiltering(config, env);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'AllowedHosts' must not contain wildcard ('*')*");
    }

    [Theory]
    [InlineData("api.assetblock.com")]
    [InlineData("api.assetblock.com;assetblock.com")]
    [InlineData("api.assetblock.com; assetblock.com; custom.domain.io")]
    public void AddAssetBlockHostFiltering_WhenProductionAndExplicitHosts_ShouldSucceed(string allowedHosts)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(allowedHosts);
        var env = CreateEnvironment(Environments.Production);

        var act = () => services.AddAssetBlockHostFiltering(config, env);

        act.Should().NotThrow();
    }

    private static IConfiguration BuildConfiguration(string? allowedHosts)
    {
        var dict = new Dictionary<string, string?>();
        if (allowedHosts is not null)
        {
            dict["AllowedHosts"] = allowedHosts;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static IHostEnvironment CreateEnvironment(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }
}
