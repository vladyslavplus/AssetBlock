using System.Runtime.CompilerServices;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>
/// Opt-in Ryuk disable. Default leaves Testcontainers Ryuk enabled.
/// Set <c>ASSETBLOCK_DISABLE_RYUK=true</c> only when Docker Desktop wedges on Ryuk start.
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
