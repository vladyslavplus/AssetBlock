using AssetBlock.Domain.Core.Payments;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Payments;

public sealed class IsoCurrencyTests
{
    [Theory]
    [InlineData("usd", "usd")]
    [InlineData("USD", "usd")]
    [InlineData("EuR", "eur")]
    public void TryNormalize_WhenThreeAsciiLetters_ShouldReturnLowercase(string raw, string expected)
    {
        var ok = IsoCurrency.TryNormalize(raw, out var currency);

        ok.Should().BeTrue();
        currency.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("us")]
    [InlineData("usdd")]
    [InlineData("us1")]
    [InlineData("12a")]
    [InlineData("us ")]
    [InlineData(" usd")]
    [InlineData("uѕd")] // Cyrillic 'ѕ'
    public void TryNormalize_WhenInvalid_ShouldReturnFalse(string? raw)
    {
        var ok = IsoCurrency.TryNormalize(raw, out var currency);

        ok.Should().BeFalse();
        currency.Should().BeEmpty();
    }
}
