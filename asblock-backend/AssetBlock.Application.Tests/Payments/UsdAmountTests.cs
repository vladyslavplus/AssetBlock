using AssetBlock.Domain.Core.Payments;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Payments;

public sealed class UsdAmountTests
{
    [Fact]
    public void FromCents_WithZero_ShouldReturnZeroAmount()
    {
        var amount = UsdAmount.FromCents(0);

        amount.Cents.Should().Be(0);
        amount.Dollars.Should().Be(0m);
    }

    [Fact]
    public void FromCents_WithPositiveCents_ShouldConvertCorrectlyToDollars()
    {
        var amount = UsdAmount.FromCents(12345);

        amount.Cents.Should().Be(12345);
        amount.Dollars.Should().Be(123.45m);
    }

    [Fact]
    public void FromDollarsExact_WithZero_ShouldReturnZeroAmount()
    {
        var amount = UsdAmount.FromDollarsExact(0m);

        amount.Cents.Should().Be(0);
        amount.Dollars.Should().Be(0m);
    }

    [Fact]
    public void FromDollarsExact_WithValidAmount_ShouldConvertCorrectlyToCents()
    {
        var amount = UsdAmount.FromDollarsExact(99.99m);

        amount.Cents.Should().Be(9999);
        amount.Dollars.Should().Be(99.99m);
    }

    [Fact]
    public void FromDollarsExact_WithTrailingZeroes_ShouldBeAcceptedAndExact()
    {
        var amount = UsdAmount.FromDollarsExact(50.00m);

        amount.Cents.Should().Be(5000);
        amount.Dollars.Should().Be(50m);
    }

    [Fact]
    public void FromDollarsExact_WithOneCent_ShouldReturnOneCent()
    {
        var amount = UsdAmount.FromDollarsExact(0.01m);

        amount.Cents.Should().Be(1);
        amount.Dollars.Should().Be(0.01m);
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(0.005)]
    [InlineData(12.3456)]
    [InlineData(0.0001)]
    public void FromDollarsExact_WithSubCentPrecision_ShouldThrowArgumentException(decimal subCentDollars)
    {
        Func<UsdAmount> act = () => UsdAmount.FromDollarsExact(subCentDollars);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*at most two decimal places*");
    }

    [Fact]
    public void FromDollarsExact_DoesNotPerformRounding()
    {
        // 1.004m would round to 1.00m if rounded, but exact must reject it.
        Func<UsdAmount> act = () => UsdAmount.FromDollarsExact(1.004m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromDollarsRounded_WithAwayFromZero_ShouldRoundHalfUpCorrectly()
    {
        var amount1 = UsdAmount.FromDollarsRounded(10.005m, MidpointRounding.AwayFromZero);
        var amount2 = UsdAmount.FromDollarsRounded(10.004m, MidpointRounding.AwayFromZero);

        amount1.Cents.Should().Be(1001);
        amount1.Dollars.Should().Be(10.01m);

        amount2.Cents.Should().Be(1000);
        amount2.Dollars.Should().Be(10.00m);
    }

    [Fact]
    public void FromDollarsRounded_WithDifferentRoundingModes_ShouldHonorProvidedMode()
    {
        // 10.005m is 1000.5 cents
        var awayFromZero = UsdAmount.FromDollarsRounded(10.005m, MidpointRounding.AwayFromZero);
        var toZero = UsdAmount.FromDollarsRounded(10.005m, MidpointRounding.ToZero);
        var toEven = UsdAmount.FromDollarsRounded(10.005m, MidpointRounding.ToEven); // 1000.5 -> 1000 (even)
        var toEvenOdd = UsdAmount.FromDollarsRounded(10.015m, MidpointRounding.ToEven); // 1001.5 -> 1002 (even)

        awayFromZero.Cents.Should().Be(1001);
        toZero.Cents.Should().Be(1000);
        toEven.Cents.Should().Be(1000);
        toEvenOdd.Cents.Should().Be(1002);
    }

    [Fact]
    public void FromCents_WithNegativeCents_ShouldThrowArgumentOutOfRangeException()
    {
        Func<UsdAmount> act = () => UsdAmount.FromCents(-1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*negative*");
    }

    [Fact]
    public void FromDollarsExact_WithNegativeDollars_ShouldThrowArgumentOutOfRangeException()
    {
        Func<UsdAmount> act = () => UsdAmount.FromDollarsExact(-0.01m);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*negative*");
    }

    [Fact]
    public void FromDollarsRounded_WithNegativeDollars_ShouldThrowArgumentOutOfRangeException()
    {
        Func<UsdAmount> act = () => UsdAmount.FromDollarsRounded(-0.005m, MidpointRounding.AwayFromZero);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*negative*");
    }

    [Fact]
    public void FromDollarsExact_AtUpperSafeBoundary_ShouldSucceed()
    {
        const decimal maxDollars = 92233720368547758.07m; // (decimal)long.MaxValue / 100m
        var amount = UsdAmount.FromDollarsExact(maxDollars);

        amount.Cents.Should().Be(long.MaxValue);
        amount.Dollars.Should().Be(maxDollars);
    }

    [Fact]
    public void FromDollarsExact_BeyondSafeBoundary_ShouldThrowArgumentOutOfRangeExceptionWithoutRawOverflow()
    {
        const decimal beyondMaxDollars = 92233720368547758.08m;
        Func<UsdAmount> act = () => UsdAmount.FromDollarsExact(beyondMaxDollars);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*exceeds the maximum supported value*");
    }

    [Fact]
    public void FromDollarsExact_WithDecimalMaxValue_ShouldThrowArgumentOutOfRangeExceptionWithoutRawOverflow()
    {
        Func<UsdAmount> act = () => UsdAmount.FromDollarsExact(decimal.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*exceeds the maximum supported value*");
    }

    [Fact]
    public void FromDollarsRounded_BeyondSafeBoundary_ShouldThrowArgumentOutOfRangeExceptionWithoutRawOverflow()
    {
        const decimal beyondMaxDollars = 92233720368547758.08m;
        Func<UsdAmount> act = () => UsdAmount.FromDollarsRounded(beyondMaxDollars, MidpointRounding.AwayFromZero);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*exceeds the maximum supported value*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(123456789)]
    public void CentsDollarsRoundTrip_ShouldBeExact(long cents)
    {
        var amountFromCents = UsdAmount.FromCents(cents);
        var amountFromDollars = UsdAmount.FromDollarsExact(amountFromCents.Dollars);

        amountFromDollars.Should().Be(amountFromCents);
        amountFromDollars.Cents.Should().Be(cents);
    }

    [Fact]
    public void Equality_BasedOnCents_ShouldBehaveCorrectly()
    {
        var a = UsdAmount.FromCents(500);
        var b = UsdAmount.FromDollarsExact(5.00m);
        var c = UsdAmount.FromCents(501);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1.5, true)]
    [InlineData(1.55, true)]
    [InlineData(1.555, false)]
    [InlineData(1.0001, false)]
    public void HasAtMostTwoDecimalPlaces_ShouldReturnExpected(decimal value, bool expected)
    {
        UsdAmount.HasAtMostTwoDecimalPlaces(value).Should().Be(expected);
    }
}
