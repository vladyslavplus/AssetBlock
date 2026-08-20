using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Extensions;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.WebApi.Tests;

public sealed class DataProtectionExtensionsTests
{
    [Fact]
    public void EnsureDedicatedKeyRingDirectory_WhenLeafNotDedicated_ShouldThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-a-key-ring-folder", Guid.NewGuid().ToString("N"));
        var act = () => DataProtectionExtensions.EnsureDedicatedKeyRingDirectory(path);
        act.Should().Throw<InvalidOperationException>().WithMessage("*dedicated*");
    }

    [Fact]
    public void EnsureDedicatedKeyRingDirectory_WhenNewPath_ShouldCreateMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), $"assetblock-dataprotection-keys-{Guid.NewGuid():N}");
        try
        {
            var dir = DataProtectionExtensions.EnsureDedicatedKeyRingDirectory(path);
            dir.Exists.Should().BeTrue();
            File.Exists(Path.Combine(path, DataProtectionExtensions.KEY_RING_MARKER_FILE_NAME)).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureDedicatedKeyRingDirectory_WhenExistingWithUnexpectedFiles_ShouldThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"assetblock-dataprotection-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "other-secret.txt"), "nope");
        try
        {
            var act = () => DataProtectionExtensions.EnsureDedicatedKeyRingDirectory(path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*unexpected*");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ResolveProtectionMode_WhenEmptyOnWindows_ShouldBeDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = DataProtectionExtensions.ResolveProtectionMode(string.Empty, new FakeHostEnvironment(Environments.Production));
        mode.Should().Be(DataProtectionOptions.MODE_DPAPI);
    }

    [Fact]
    public void ResolveProtectionMode_WhenNoneInProduction_IsAllowedAsConfiguredValue()
    {
        // Explicit None is resolved here; ApplyProtector rejects it in Production.
        var mode = DataProtectionExtensions.ResolveProtectionMode(
            DataProtectionOptions.MODE_NONE,
            new FakeHostEnvironment(Environments.Production));
        mode.Should().Be(DataProtectionOptions.MODE_NONE);
    }

    [Fact]
    public void ResolveProtectionMode_WhenEmptyProductionNonWindows_ShouldThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var act = () => DataProtectionExtensions.ResolveProtectionMode(
            string.Empty,
            new FakeHostEnvironment(Environments.Production));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Certificate or Kms*");
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "AssetBlock.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
