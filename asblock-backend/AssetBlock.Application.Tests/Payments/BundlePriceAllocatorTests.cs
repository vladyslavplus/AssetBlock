using AssetBlock.Domain.Core.Payments;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Payments;

public class BundlePriceAllocatorTests
{
    [Fact]
    public void Allocate_WhenUnevenWeights_ShouldSumExactlyAndPreferHeavierItems()
    {
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, UsdAmount.FromCents(1000)),
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, UsdAmount.FromCents(3000))
        ];

        IReadOnlyList<BundlePriceAllocator.AllocationResult> result = BundlePriceAllocator.Allocate(UsdAmount.FromCents(1000), items);

        result.Should().HaveCount(2);
        result.Sum(r => r.AllocatedPrice.Cents).Should().Be(1000);
        result.Should().OnlyContain(r => r.AllocatedPrice.Cents >= 1);
        result.Single(r => r.Position == 2).AllocatedPrice.Cents.Should().BeGreaterThan(
            result.Single(r => r.Position == 1).AllocatedPrice.Cents);
    }

    [Fact]
    public void Allocate_WhenEqualWeights_ShouldSplitEvenly()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(a, 1, UsdAmount.FromCents(500)),
            new(b, 2, UsdAmount.FromCents(500))
        ];

        IReadOnlyList<BundlePriceAllocator.AllocationResult> result = BundlePriceAllocator.Allocate(UsdAmount.FromCents(100), items);

        result.Select(r => r.AllocatedPrice.Cents).Should().Equal(50L, 50L);
    }

    [Fact]
    public void Allocate_WhenOneCentRemainder_ShouldGiveExtraCentByTieBreak()
    {
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(higherId, 1, UsdAmount.FromCents(100)),
            new(lowerId, 2, UsdAmount.FromCents(100))
        ];

        // total 3 cents: 1 reserved each, 1 remainder → goes to position 1 (then lower asset id on equal remainder)
        IReadOnlyList<BundlePriceAllocator.AllocationResult> result = BundlePriceAllocator.Allocate(UsdAmount.FromCents(3), items);

        result.Sum(r => r.AllocatedPrice.Cents).Should().Be(3);
        result.Single(r => r.Position == 1).AllocatedPrice.Cents.Should().Be(2);
        result.Single(r => r.Position == 2).AllocatedPrice.Cents.Should().Be(1);
    }

    [Fact]
    public void Allocate_WhenTotalEqualsItemCount_ShouldGiveOneCentEach()
    {
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(Guid.NewGuid(), 1, UsdAmount.FromCents(999)),
            new(Guid.NewGuid(), 2, UsdAmount.FromCents(1))
        ];

        IReadOnlyList<BundlePriceAllocator.AllocationResult> result = BundlePriceAllocator.Allocate(UsdAmount.FromCents(2), items);

        result.Should().OnlyContain(r => r.AllocatedPrice.Cents == 1);
    }

    [Fact]
    public void Allocate_WhenDuplicateAsset_ShouldThrow()
    {
        var id = Guid.NewGuid();
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(id, 1, UsdAmount.FromCents(100)),
            new(id, 2, UsdAmount.FromCents(100))
        ];

        Func<IReadOnlyList<BundlePriceAllocator.AllocationResult>> act = () => BundlePriceAllocator.Allocate(UsdAmount.FromCents(10), items);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_WhenTotalTooSmall_ShouldThrow()
    {
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(Guid.NewGuid(), 1, UsdAmount.FromCents(100)),
            new(Guid.NewGuid(), 2, UsdAmount.FromCents(100))
        ];

        Func<IReadOnlyList<BundlePriceAllocator.AllocationResult>> act = () => BundlePriceAllocator.Allocate(UsdAmount.FromCents(1), items);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Allocate_WhenTotalIsAtMaxBound_ShouldSucceedAndSumExactly()
    {
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, UsdAmount.FromCents(5000)),
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, UsdAmount.FromCents(5000))
        ];

        IReadOnlyList<BundlePriceAllocator.AllocationResult> result = BundlePriceAllocator.Allocate(UsdAmount.FromCents(BundlePriceAllocator.MAX_AMOUNT_CENTS), items);

        result.Sum(r => r.AllocatedPrice.Cents).Should().Be(BundlePriceAllocator.MAX_AMOUNT_CENTS);
        result.Should().OnlyContain(r => r.AllocatedPrice.Cents >= 1);
    }

    [Fact]
    public void Allocate_WhenTotalExceedsMaxBound_ShouldThrow()
    {
        BundlePriceAllocator.AllocationInput[] items =
        [
            new(Guid.NewGuid(), 1, UsdAmount.FromCents(100)),
            new(Guid.NewGuid(), 2, UsdAmount.FromCents(100))
        ];

        Func<IReadOnlyList<BundlePriceAllocator.AllocationResult>> act = () => BundlePriceAllocator.Allocate(UsdAmount.FromCents(BundlePriceAllocator.MAX_AMOUNT_CENTS + 1), items);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
