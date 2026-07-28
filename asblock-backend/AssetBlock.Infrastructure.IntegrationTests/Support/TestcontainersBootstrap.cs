using System.Runtime.CompilerServices;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Opt-in Ryuk disable for wedged Docker Desktop (containers stuck in Created).
/// Default keeps Ryuk enabled so crash/kill still cleans Testcontainers resources.
/// Set <c>ASSETBLOCK_DISABLE_RYUK=true</c> (or <c>1</c>) only when needed locally.
/// </summary>
internal static class TestcontainersBootstrap
{
    private const string DISABLE_RYUK_ENV = "ASSETBLOCK_DISABLE_RYUK";

    [ModuleInitializer]
    internal static void Init()
    {
        var raw = Environment.GetEnvironmentVariable(DISABLE_RYUK_ENV);
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "1", StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
        }
    }
}
