using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class AesGcmEncryptionServiceTests
{
    private static AesGcmEncryptionService CreateService()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions { KeyBase64 = key }));
    }

    [Fact]
    public async Task EncryptDecrypt_roundtrip_empty_plain()
    {
        var sut = CreateService();
        await using var plain = new MemoryStream();
        await using var cipher = new MemoryStream();
        await sut.Encrypt(plain, cipher);
        cipher.Position = 0;
        await using var outPlain = new MemoryStream();
        await sut.Decrypt(cipher, outPlain);
        outPlain.ToArray().Should().BeEmpty();
    }

    [Fact]
    public async Task EncryptDecrypt_roundtrip_multi_chunk()
    {
        var sut = CreateService();
        var data = new byte[1024 * 1024 + 100];
        RandomNumberGenerator.Fill(data);
        await using var plain = new MemoryStream(data);
        await using var cipher = new MemoryStream();
        await sut.Encrypt(plain, cipher);
        cipher.Position = 0;
        await using var outPlain = new MemoryStream();
        await sut.Decrypt(cipher, outPlain);
        outPlain.ToArray().Should().Equal(data);
    }

    [Fact]
    public void GetKey_throws_when_missing()
    {
        var sut = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions { KeyBase64 = "" }));
        var act = async () =>
        {
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            await using var o = new MemoryStream();
            await sut.Encrypt(ms, o);
        };
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void GetKey_throws_on_bad_length()
    {
        var sut = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions { KeyBase64 = Convert.ToBase64String(new byte[5]) }));
        var act = async () =>
        {
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            await using var o = new MemoryStream();
            await sut.Encrypt(ms, o);
        };
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
