using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Extensions;

public class ObservabilityExtensionsTests
{
    private readonly IServiceCollection _services;
    private readonly IHostEnvironment _environment;

    public ObservabilityExtensionsTests()
    {
        _services = new ServiceCollection();
        _environment = Substitute.For<IHostEnvironment>();
        _environment.EnvironmentName.Returns("Test");
    }

    [Fact]
    public void AddAssetBlockObservability_WhenDisabled_ShouldNotRegisterOpenTelemetry()
    {
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Observability:Enabled", "false" }
            });
        var config = configBuilder.Build();

        _services.AddAssetBlockObservability(config, _environment);

        _services.Should().NotContain(s => s.ServiceType.FullName != null && s.ServiceType.FullName.Contains("OpenTelemetry"));
    }

    [Fact]
    public void AddAssetBlockObservability_WhenEnabled_ShouldRegisterOpenTelemetry()
    {
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Observability:Enabled", "true" },
                { "Observability:ServiceName", "TestService" },
                { "Observability:OtlpEndpoint", "http://127.0.0.1:4317" }
            });
        var config = configBuilder.Build();

        _services.AddAssetBlockObservability(config, _environment);

        _services.Should().Contain(s => s.ServiceType.FullName != null && s.ServiceType.FullName.Contains("OpenTelemetry"));
    }
}
