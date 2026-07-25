using AssetBlock.Domain.Core.Payments;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Payments;

public class BundlePriceAllocatorTests
{
    [Fact]
    public void Allocate_WhenUnevenWeights_ShouldSumExactlyAndPreferHeavierItems()
    {
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, 1000),
            new BundlePriceAllocator.AllocationInput(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, 3000)
        };

        var result = BundlePriceAllocator.Allocate(1000, items);

        result.Should().HaveCount(2);
        result.Sum(r => r.AllocatedCents).Should().Be(1000);
        result.Should().OnlyContain(r => r.AllocatedCents >= 1);
        result.Single(r => r.Position == 2).AllocatedCents.Should().BeGreaterThan(
            result.Single(r => r.Position == 1).AllocatedCents);
    }

    [Fact]
    public void Allocate_WhenEqualWeights_ShouldSplitEvenly()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(a, 1, 500),
            new BundlePriceAllocator.AllocationInput(b, 2, 500)
        };

        var result = BundlePriceAllocator.Allocate(100, items);

        result.Select(r => r.AllocatedCents).Should().Equal(50L, 50L);
    }

    [Fact]
    public void Allocate_WhenOneCentRemainder_ShouldGiveExtraCentByTieBreak()
    {
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(higherId, 1, 100),
            new BundlePriceAllocator.AllocationInput(lowerId, 2, 100)
        };

        // total 3 cents: 1 reserved each, 1 remainder → goes to position 1 (then lower asset id on equal remainder)
        var result = BundlePriceAllocator.Allocate(3, items);

        result.Sum(r => r.AllocatedCents).Should().Be(3);
        result.Single(r => r.Position == 1).AllocatedCents.Should().Be(2);
        result.Single(r => r.Position == 2).AllocatedCents.Should().Be(1);
    }

    [Fact]
    public void Allocate_WhenTotalEqualsItemCount_ShouldGiveOneCentEach()
    {
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 1, 999),
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 2, 1)
        };

        var result = BundlePriceAllocator.Allocate(2, items);

        result.Should().OnlyContain(r => r.AllocatedCents == 1);
    }

    [Fact]
    public void Allocate_WhenDuplicateAsset_ShouldThrow()
    {
        var id = Guid.NewGuid();
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(id, 1, 100),
            new BundlePriceAllocator.AllocationInput(id, 2, 100)
        };

        var act = () => BundlePriceAllocator.Allocate(10, items);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_WhenTotalTooSmall_ShouldThrow()
    {
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 1, 100),
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 2, 100)
        };

        var act = () => BundlePriceAllocator.Allocate(1, items);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToCents_WhenSubCentPrecision_ShouldThrow()
    {
        var act = () => BundlePriceAllocator.ToCents(1.001m);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToCentsAndFromCents_WhenValid_ShouldRoundTrip()
    {
        BundlePriceAllocator.FromCents(BundlePriceAllocator.ToCents(12.34m)).Should().Be(12.34m);
    }

    [Fact]
    public void ToCents_WhenAtMaxAmount_ShouldSucceed()
    {
        var maxDecimal = BundlePriceAllocator.FromCents(BundlePriceAllocator.MAX_AMOUNT_CENTS);
        var cents = BundlePriceAllocator.ToCents(maxDecimal);
        cents.Should().Be(BundlePriceAllocator.MAX_AMOUNT_CENTS);
    }

    [Fact]
    public void ToCents_WhenOverMaxAmount_ShouldThrow()
    {
        var overMax = BundlePriceAllocator.FromCents(BundlePriceAllocator.MAX_AMOUNT_CENTS) + 0.01m;
        var act = () => BundlePriceAllocator.ToCents(overMax);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Theory: covers all rejection variants + valid max in one parameterized pass.
    [Theory]
    [InlineData(false, "1.001")]           // sub-cent precision → ArgumentException
    [InlineData(false, "1000000.00")]      // over MAX_AMOUNT (999999.99) → ArgumentOutOfRangeException
    [InlineData(false, "79228162514264337593543950335")] // decimal.MaxValue → ArgumentOutOfRangeException
    [InlineData(true,  "999999.99")]       // valid max → should succeed
    [InlineData(true,  "0.01")]            // valid min → should succeed
    public void ToCents_BoundaryTheory(bool shouldSucceed, string amountStr)
    {
        var amount = decimal.Parse(amountStr, System.Globalization.CultureInfo.InvariantCulture);
        if (shouldSucceed)
        {
            var act = () => BundlePriceAllocator.ToCents(amount);
            act.Should().NotThrow();
        }
        else
        {
            var act = () => BundlePriceAllocator.ToCents(amount);
            act.Should().Throw<Exception>("amount {0} should be rejected", amount);
        }
    }

    [Fact]
    public void Allocate_WhenTotalIsAtMaxBound_ShouldSucceedAndSumExactly()
    {
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, 5000),
            new BundlePriceAllocator.AllocationInput(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, 5000)
        };

        var result = BundlePriceAllocator.Allocate(BundlePriceAllocator.MAX_AMOUNT_CENTS, items);

        result.Sum(r => r.AllocatedCents).Should().Be(BundlePriceAllocator.MAX_AMOUNT_CENTS);
        result.Should().OnlyContain(r => r.AllocatedCents >= 1);
    }

    [Fact]
    public void Allocate_WhenTotalExceedsMaxBound_ShouldThrow()
    {
        var items = new[]
        {
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 1, 100),
            new BundlePriceAllocator.AllocationInput(Guid.NewGuid(), 2, 100)
        };

        var act = () => BundlePriceAllocator.Allocate(BundlePriceAllocator.MAX_AMOUNT_CENTS + 1, items);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
