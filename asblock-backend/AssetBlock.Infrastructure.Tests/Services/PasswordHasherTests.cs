using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    [Fact]
    public void Hash_and_Verify_roundtrip()
    {
        var hash = _sut.Hash("secret123");
        hash.Should().NotBeNullOrEmpty();
        _sut.Verify("secret123", hash).Should().BeTrue();
        _sut.Verify("wrong", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_uses_work_factor_12()
    {
        var hash = _sut.Hash("secret123");
        // BCrypt hash format: $2a$12$... or $2b$12$...
        var parts = hash.Split('$');
        parts.Length.Should().BeGreaterThanOrEqualTo(3);
        parts[2].Should().Be("12");
        _sut.NeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void NeedsRehash_when_lower_cost_hash_returns_true()
    {
        // Generate a hash with lower work factor (cost 10)
        var lowCostHash = BCrypt.Net.BCrypt.HashPassword("secret123", workFactor: 10);
        _sut.Verify("secret123", lowCostHash).Should().BeTrue();
        _sut.NeedsRehash(lowCostHash).Should().BeTrue();
    }

    [Fact]
    public void NeedsRehash_when_cost_12_or_higher_returns_false()
    {
        var cost12Hash = BCrypt.Net.BCrypt.HashPassword("secret123", workFactor: 12);
        _sut.NeedsRehash(cost12Hash).Should().BeFalse();
    }
}
