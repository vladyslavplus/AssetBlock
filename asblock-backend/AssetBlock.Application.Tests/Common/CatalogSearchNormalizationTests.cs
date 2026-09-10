using AssetBlock.Application.Common;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Common;

public class CatalogSearchNormalizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSearchQuery_WhenNullOrWhitespace_ShouldReturnNull(string? input)
    {
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);
        result.Should().BeNull();
    }

    [Fact]
    public void NormalizeSearchQuery_WhenNormalText_ShouldNormalizeWhitespace()
    {
        const string input = "   medieval   fantasy   sword   ";
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);
        result.Should().Be("medieval fantasy sword");
    }

    [Fact]
    public void NormalizeSearchQuery_WhenUkrainianText_ShouldPreserveCyrillic()
    {
        const string input = "  Лицарський   дворучний   меч  ";
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);
        result.Should().Be("Лицарський дворучний меч");
    }

    [Fact]
    public void NormalizeSearchQuery_WhenFullWidthCharacters_ShouldNormalizeViaNfkc()
    {
        // Full-width Latin 'ｓｗｏｒｄ' (\uFF53\uFF57\uFF4F\uFF52\uFF44)
        const string input = "\uFF53\uFF57\uFF4F\uFF52\uFF44";
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);
        result.Should().Be("sword");
    }

    [Fact]
    public void NormalizeSearchQuery_WhenExceeds256Scalars_ShouldTruncateAtScalarBoundary()
    {
        // 300 ASCII characters
        var input = new string('a', 300);
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);

        result.Should().NotBeNull();
        CatalogSearchNormalization.CountUnicodeScalars(result).Should().Be(256);
        result.Length.Should().Be(256);
    }

    [Fact]
    public void NormalizeSearchQuery_WhenContainsEmojis_ShouldCountRunesCorrectly()
    {
        // 🛡️ (shield) is a surrogate pair (2 UTF-16 chars, 1 Rune or 2 with variation selector)
        const string input = "sword ⚔️ shield";
        var result = CatalogSearchNormalization.NormalizeSearchQuery(input);
        result.Should().NotBeNull();
        result.Should().Contain("sword");
        result.Should().Contain("shield");
    }

    [Theory]
    [InlineData("valid search")]
    [InlineData("пошук українською")]
    [InlineData("C# .NET Unity 3D")]
    public void BeWithinUnicodeScalarLimit_WhenWithinLimit_ShouldReturnTrue(string input)
    {
        CatalogSearchNormalization.BeWithinUnicodeScalarLimit(input).Should().BeTrue();
    }

    [Fact]
    public void BeWithinUnicodeScalarLimit_WhenExact256Scalars_ShouldReturnTrue()
    {
        var input = new string('x', 256);
        CatalogSearchNormalization.BeWithinUnicodeScalarLimit(input).Should().BeTrue();
    }

    [Fact]
    public void BeWithinUnicodeScalarLimit_When257Scalars_ShouldReturnFalse()
    {
        var input = new string('x', 257);
        CatalogSearchNormalization.BeWithinUnicodeScalarLimit(input).Should().BeFalse();
    }

    [Fact]
    public void NotContainInvalidControlCharacters_WhenHasNullByte_ShouldReturnFalse()
    {
        const string input = "search\0query";
        CatalogSearchNormalization.NotContainInvalidControlCharacters(input).Should().BeFalse();
    }

    [Fact]
    public void NotContainInvalidControlCharacters_WhenHasEscapeChar_ShouldReturnFalse()
    {
        const string input = "search\u001bquery";
        CatalogSearchNormalization.NotContainInvalidControlCharacters(input).Should().BeFalse();
    }

    [Fact]
    public void NotContainInvalidControlCharacters_WhenHasBellChar_ShouldReturnFalse()
    {
        const string input = "search\u0007query";
        CatalogSearchNormalization.NotContainInvalidControlCharacters(input).Should().BeFalse();
    }

    [Theory]
    [InlineData("search\tquery")]
    [InlineData("search\rquery")]
    [InlineData("search\nquery")]
    [InlineData("search\r\nquery")]
    public void NotContainInvalidControlCharacters_WhenHasAllowedWhitespaceControl_ShouldReturnTrue(string input)
    {
        CatalogSearchNormalization.NotContainInvalidControlCharacters(input).Should().BeTrue();
    }
}
