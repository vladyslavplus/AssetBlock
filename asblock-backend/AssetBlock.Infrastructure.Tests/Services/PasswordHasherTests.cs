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
}
