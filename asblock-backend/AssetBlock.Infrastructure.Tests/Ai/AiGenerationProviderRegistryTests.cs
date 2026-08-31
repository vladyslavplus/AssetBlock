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
        IAiGenerationProvider first = Substitute.For<IAiGenerationProvider>();
        first.Kind.Returns(AiProviderKind.OPENROUTER);
        IAiGenerationProvider second = Substitute.For<IAiGenerationProvider>();
        second.Kind.Returns(AiProviderKind.OPENROUTER);

        Func<AiGenerationProviderRegistry> act = () => new AiGenerationProviderRegistry([first, second]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*OPENROUTER*");
    }
}
