using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.WebApi.HealthChecks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace AssetBlock.WebApi.Tests;

public sealed class StorageHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenListObjectsSucceeds_ShouldBeHealthy()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        storage.ListObjects(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Domain.Core.Primitives.Storage.StorageObjectInfo>()));

        StorageHealthCheck sut = CreateSut(storage);
        HealthCheckResult result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        storage.Received(1).ListObjects("__health__/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_WhenListObjectsFails_ShouldBeUnhealthy()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        storage.ListObjects(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingAsyncEnumerable<Domain.Core.Primitives.Storage.StorageObjectInfo>(new InvalidOperationException("down")));

        StorageHealthCheck sut = CreateSut(storage);
        HealthCheckResult result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("storage provider");
    }

    private static StorageHealthCheck CreateSut(IAssetStorageService storage)
    {
        var services = new ServiceCollection();
        services.AddSingleton(storage);
        return new StorageHealthCheck(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (T? item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception ex)
    {
        await Task.Yield();
        if (ex is not null)
        {
            throw ex;
        }
        yield break;
    }
}
