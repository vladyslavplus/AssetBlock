using System.Text;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class AssetProcessingJobStoreTests
{
    [Fact]
    public void BoundErrorSummary_WhenNull_ThrowsArgumentNullException()
    {
        var act = () => AssetProcessingJobStore.BoundErrorSummary(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Short error message")]
    [InlineData("SCAN_FAILED: Virus detected in archive")]
    public void BoundErrorSummary_WhenUnderLimit_ReturnsOriginalString(string errorSummary)
    {
        var result = AssetProcessingJobStore.BoundErrorSummary(errorSummary);
        result.Should().Be(errorSummary);
    }

    [Fact]
    public void BoundErrorSummary_With1999AsciiAndEmojiAndSuffix_ShouldPreserveEmojiWithoutSplittingSurrogates()
    {
        // 1999 ASCII characters
        var asciiPrefix = new string('x', 1999);
        const string emoji = "🚀"; // U+1F680 (2 UTF-16 code units)
        const string suffix = "_SHOULD_BE_TRUNCATED";

        var input = asciiPrefix + emoji + suffix;

        var bounded = AssetProcessingJobStore.BoundErrorSummary(input);

        // Must contain exactly 2000 Unicode scalar values (runes)
        bounded.EnumerateRunes().Count().Should().Be(2000);
        bounded.Length.Should().Be(2001); // 1999 ASCII (1 unit each) + 1 emoji (2 units)
        bounded.Should().StartWith(asciiPrefix);
        bounded.Should().EndWith(emoji);
        bounded.Should().NotContain("SHOULD_BE_TRUNCATED");

        // Verify valid UTF-8 encoding
        var bytes = Encoding.UTF8.GetBytes(bounded);
        var roundtripped = Encoding.UTF8.GetString(bytes);
        roundtripped.Should().Be(bounded);

        // Verify no broken/lone surrogates
        for (var i = 0; i < bounded.Length; i++)
        {
            if (char.IsSurrogate(bounded[i]))
            {
                char.IsHighSurrogate(bounded[i]).Should().BeTrue();
                (i + 1).Should().BeLessThan(bounded.Length);
                char.IsLowSurrogate(bounded[i + 1]).Should().BeTrue();
                i++; // Skip the low surrogate
            }
        }
    }

    [Fact]
    public void BoundErrorSummary_WithManyEmojis_ShouldBoundTo2000Runes()
    {
        // 2500 emojis (each is 2 UTF-16 code units, U+1F680)
        var input = string.Concat(Enumerable.Repeat("🚀", 2500));

        var bounded = AssetProcessingJobStore.BoundErrorSummary(input);

        bounded.EnumerateRunes().Count().Should().Be(2000);
        bounded.Length.Should().Be(4000); // 2000 * 2 UTF-16 code units
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(bounded)).Should().Be(bounded);
    }
}
