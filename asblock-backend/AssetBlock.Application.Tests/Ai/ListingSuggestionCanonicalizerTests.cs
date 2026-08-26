using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Dto;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Ai;

public sealed class ListingSuggestionCanonicalizerTests
{
    [Fact]
    public void ComputeContentHash_ShouldBeStableLowercaseSha256()
    {
        var suggestion = new ListingSuggestion("Title", "Desc", "3D", ["lowpoly"]);
        var hash = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion);

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[a-f0-9]{64}$");
        ListingSuggestionCanonicalizer.ComputeContentHash(suggestion).Should().Be(hash);
    }
}
