namespace AssetBlock.Infrastructure.Tests.Observability;

[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class AssetBlockDiagnosticsCollection
{
    public const string NAME = "AssetBlock diagnostics";
}
