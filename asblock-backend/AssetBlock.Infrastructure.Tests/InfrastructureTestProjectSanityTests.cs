namespace AssetBlock.Infrastructure.Tests;

/// <summary>Sanity check that the test project references Infrastructure and the toolchain restores.</summary>
public class InfrastructureTestProjectSanityTests
{
    [Fact]
    public void Infrastructure_assembly_should_load()
    {
        var asm = typeof(DependencyInjection).Assembly;
        asm.GetName().Name.Should().Be("AssetBlock.Infrastructure");
    }
}
