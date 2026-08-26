using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.HealthChecks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.WebApi.Tests;

public sealed class ClamAvHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenSignaturesAreFresh_ShouldBeHealthy()
    {
        var scanner = Substitute.For<IContentMalwareScanner>();
        scanner.GetSignatureState(Arg.Any<CancellationToken>()).Returns(
            MalwareScannerSignatureState.FromBuiltAt(
                DateTimeOffset.UtcNow.AddHours(-2),
                TimeSpan.FromHours(72),
                DateTimeOffset.UtcNow));
        var sut = new ClamAvHealthCheck(scanner);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDaemonUnavailable_ShouldBeUnhealthyWithoutHostDetails()
    {
        var scanner = Substitute.For<IContentMalwareScanner>();
        scanner.GetSignatureState(Arg.Any<CancellationToken>()).Returns(MalwareScannerSignatureState.Unavailable());
        var sut = new ClamAvHealthCheck(scanner);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotBeNull();
        result.Description.Should().NotContain("127.0.0.1");
        result.Description.Should().NotContain("3310");
        result.Description.Should().Be("Malware scanner readiness check failed.");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenSignaturesAreStale_ShouldBeUnhealthy()
    {
        var scanner = Substitute.For<IContentMalwareScanner>();
        scanner.GetSignatureState(Arg.Any<CancellationToken>()).Returns(
            MalwareScannerSignatureState.FromBuiltAt(
                DateTimeOffset.UtcNow.AddDays(-5),
                TimeSpan.FromHours(72),
                DateTimeOffset.UtcNow));
        var sut = new ClamAvHealthCheck(scanner);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Malware scanner signatures are stale.");
        result.Description.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public void AddAssetBlockHealthChecks_WhenProcessingDisabled_ShouldNotRegisterClamAv()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();

        services.AddAssetBlockHealthChecks(configuration);
        services.AddLogging();
        var registrations = services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.Should().NotContain(r => r.Name == "clamav");
    }

    [Fact]
    public void AddAssetBlockHealthChecks_WhenProcessingEnabled_ShouldRegisterClamAvOnReadyOnly()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var services = new ServiceCollection();

        services.AddAssetBlockHealthChecks(configuration);
        services.AddLogging();
        var registrations = services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.Should().Contain(r => r.Name == "clamav" && r.Tags.Contains("ready"));
        registrations.Should().NotContain(r => r.Name == "clamav" && r.Tags.Contains("live"));
    }
}
