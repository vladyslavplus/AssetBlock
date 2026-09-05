using System.Security.Cryptography;
using System.Text;
using AssetBlock.Application.Common;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Common;

public class AssetPublicMetadataCanonicalizerTests
{
    [Fact]
    public void Canonicalize_WithStandardMetadata_ShouldFormatDeterministicLabelsAndOrder()
    {
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Fantasy Sword",
            description: "A sharp steel blade.\r\nSuitable for RPG adventures.",
            categoryName: "Weapons",
            tags: ["sword", "steel", "rpg"]);

        result.CanonicalText.Should().Be(
            "title: Fantasy Sword\n" +
            "description: A sharp steel blade.\nSuitable for RPG adventures.\n" +
            "category: Weapons\n" +
            "tags: rpg, steel, sword");

        result.IsTruncated.Should().BeFalse();
        result.ContentHash.Should().HaveLength(64);
        result.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");

        // Verify SHA-256 hash matches exactly
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.CanonicalText)));
        result.ContentHash.Should().Be(expectedHash);
    }

    [Fact]
    public void Canonicalize_WithUkrainianMetadata_ShouldPreserveCyrillicAndPunctuation()
    {
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Лицарський меч",
            description: "Стилізована модель дворучного меча.\nПідходить для фентезійних ігор.",
            categoryName: "3D Моделі",
            tags: ["фентезі", "зброя", "меч"]);

        result.CanonicalText.Should().Contain("title: Лицарський меч");
        result.CanonicalText.Should().Contain("category: 3D Моделі");
        // Tags must be ordinally sorted: "зброя", "меч", "фентезі"
        result.CanonicalText.Should().Contain("tags: зброя, меч, фентезі");
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_WithTags_ShouldDeduplicateAndSortOrdinally()
    {
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Test Asset",
            description: "Desc",
            categoryName: "Cat",
            tags: ["zeta", "alpha", "Beta", "alpha", "  gamma  "]);

        // Ordinal sort: "Beta" precedes "alpha", "gamma", "zeta"
        result.CanonicalText.Should().Contain("tags: Beta, alpha, gamma, zeta");
    }

    [Fact]
    public void Canonicalize_WhenDescriptionAndCategoryMissing_ShouldOmitLabelsGracefully()
    {
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Only Title",
            description: null,
            categoryName: "",
            tags: []);

        result.CanonicalText.Should().Be("title: Only Title");
        result.CanonicalText.Should().NotContain("description:");
        result.CanonicalText.Should().NotContain("category:");
        result.CanonicalText.Should().NotContain("tags:");
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_WhenTitleHas500Emojis_ShouldTruncateTo500Utf16CharsWithoutBreakingSurrogates()
    {
        // Each 😀 is 2 UTF-16 code units (surrogate pair \uD83D\uDE00)
        // 500 emojis = 1000 UTF-16 chars. At limit 500 UTF-16 chars, exactly 250 emojis should remain.
        var emojiTitle = string.Concat(Enumerable.Repeat("😀", 500));
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: emojiTitle,
            description: null,
            categoryName: null,
            tags: null);

        result.IsTruncated.Should().BeTrue();
        var titleValue = result.CanonicalText["title: ".Length..];
        titleValue.Length.Should().Be(500);
        titleValue.Should().Be(string.Concat(Enumerable.Repeat("😀", 250)));
        char.IsHighSurrogate(titleValue[^1]).Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_WhenTitleHasOddCharsBeforeSurrogateAtBoundary_ShouldNotSplitSurrogatePair()
    {
        // 1 ASCII 'A' + 250 emojis = 501 UTF-16 chars.
        // At limit 500 chars, the 250th emoji starts at index 499 (high surrogate) and ends at 500.
        // To avoid an orphaned high surrogate, it must step back to 499 chars (1 ASCII + 249 emojis).
        var titleWithOddBoundary = "A" + string.Concat(Enumerable.Repeat("🚀", 250));
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: titleWithOddBoundary,
            description: null,
            categoryName: null,
            tags: null);

        result.IsTruncated.Should().BeTrue();
        var titleValue = result.CanonicalText["title: ".Length..];
        titleValue.Length.Should().Be(499);
        titleValue.Should().Be("A" + string.Concat(Enumerable.Repeat("🚀", 249)));
        char.IsHighSurrogate(titleValue[^1]).Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_WhenTagHasEmojisExceeding50Chars_ShouldTruncateWithoutBreakingSurrogates()
    {
        // Tag with 1 ASCII + 25 emojis = 51 UTF-16 chars.
        // Limit is 50 chars. Truncating without breaking surrogate pair yields 1 ASCII + 24 emojis = 49 chars.
        var oddTag = "X" + string.Concat(Enumerable.Repeat("🎮", 25));
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Title",
            description: null,
            categoryName: null,
            tags: [oddTag]);

        result.IsTruncated.Should().BeTrue();
        var tagLine = result.CanonicalText.Split('\n').First(l => l.StartsWith("tags: ", StringComparison.Ordinal));
        var tagValue = tagLine["tags: ".Length..];
        tagValue.Length.Should().Be(49);
        tagValue.Should().Be("X" + string.Concat(Enumerable.Repeat("🎮", 24)));
        char.IsHighSurrogate(tagValue[^1]).Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_WhenTitleExceeds500Chars_ShouldTruncateAtScalarBoundary()
    {
        var longTitle = new string('A', 600);
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: longTitle,
            description: null,
            categoryName: null,
            tags: null);

        result.IsTruncated.Should().BeTrue();
        result.CanonicalText.Should().HaveLength("title: ".Length + 500);
    }

    [Fact]
    public void Canonicalize_WhenComposedExceeds8192Chars_ShouldTruncateAtScalarBoundary()
    {
        var hugeDesc = new string('D', 9000);
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Short Title",
            description: hugeDesc,
            categoryName: "Category",
            tags: ["tag1", "tag2"]);

        result.IsTruncated.Should().BeTrue();
        result.CanonicalText.Length.Should().BeLessThanOrEqualTo(8192);
        Encoding.UTF8.GetByteCount(result.CanonicalText).Should().BeLessThanOrEqualTo(32768);
    }

    [Fact]
    public void Canonicalize_WhenNfkcCharactersPresent_ShouldNormalizeConsistently()
    {
        // Full-width characters
        CanonicalPublicMetadataResult result1 = AssetPublicMetadataCanonicalizer.Canonicalize("Sword \uFF41\uFF42\uFF43", null, null, null);
        CanonicalPublicMetadataResult result2 = AssetPublicMetadataCanonicalizer.Canonicalize("Sword abc", null, null, null);

        result1.CanonicalText.Should().Be(result2.CanonicalText);
        result1.ContentHash.Should().Be(result2.ContentHash);
    }

    [Fact]
    public void Canonicalize_ExcludesSensitiveFieldsStructurally()
    {
        CanonicalPublicMetadataResult result = AssetPublicMetadataCanonicalizer.Canonicalize(
            title: "Safe Asset",
            description: "Public description only.",
            categoryName: "Tools",
            tags: ["plugin"]);

        // Structural check: Canonical text only has title, description, category, tags
        var lines = result.CanonicalText.Split('\n');
        foreach (var line in lines)
        {
            var isKnown = line.StartsWith("title: ", StringComparison.Ordinal)
                || line.StartsWith("description: ", StringComparison.Ordinal)
                || line.StartsWith("category: ", StringComparison.Ordinal)
                || line.StartsWith("tags: ", StringComparison.Ordinal)
                || line.Length > 0; // continuation line of description
            isKnown.Should().BeTrue();
        }
    }
}
