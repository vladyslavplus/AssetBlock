using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Ai;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Ai;

public sealed class AiGenerationProviderRegistryTests
{
    [Fact]
    public void Constructor_WhenDuplicateKind_ShouldThrow()
    {
        var first = Substitute.For<IAiGenerationProvider>();
        first.Kind.Returns(AiProviderKind.OPENROUTER);
        var second = Substitute.For<IAiGenerationProvider>();
        second.Kind.Returns(AiProviderKind.OPENROUTER);

        var act = () => new AiGenerationProviderRegistry([first, second]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*OPENROUTER*");
    }
}
