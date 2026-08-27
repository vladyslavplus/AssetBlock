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

    [Fact]
    public void ComputeContentHash_WhenTagOrderDiffers_ShouldProduceIdenticalHash()
    {
        var suggestion1 = new ListingSuggestion("Title", "Desc", "3D", ["zebra", "apple", "mango"]);
        var suggestion2 = new ListingSuggestion("Title", "Desc", "3D", ["apple", "mango", "zebra"]);
        var suggestion3 = new ListingSuggestion("Title", "Desc", "3D", ["mango", "zebra", "apple"]);

        var hash1 = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion1);
        var hash2 = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion2);
        var hash3 = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion3);

        hash1.Should().Be(hash2);
        hash2.Should().Be(hash3);
    }

    [Fact]
    public void ComputeContentHash_ShouldNotMutateCallerCollection()
    {
        string[] originalTags = ["zebra", "apple", "mango"];
        var suggestion = new ListingSuggestion("Title", "Desc", "3D", originalTags);

        _ = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion);

        originalTags[0].Should().Be("zebra");
        originalTags[1].Should().Be("apple");
        originalTags[2].Should().Be("mango");
    }

    [Fact]
    public void ComputeContentHash_WhenRepeatedCalls_ShouldRemainStable()
    {
        var suggestion = new ListingSuggestion("Title", "Desc", "3D", ["tag2", "tag1"]);

        var first = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion);
        var second = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion);
        var third = ListingSuggestionCanonicalizer.ComputeContentHash(suggestion);

        first.Should().Be(second);
        second.Should().Be(third);
    }
}
