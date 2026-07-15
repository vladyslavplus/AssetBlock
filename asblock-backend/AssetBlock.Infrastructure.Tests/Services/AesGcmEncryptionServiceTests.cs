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
    public async Task EncryptDecrypt_roundtrip_non_seekable_plain_stream()
    {
        var sut = CreateService();
        var data = RandomNumberGenerator.GetBytes(1024 * 1024 + 100);
        await using var plain = new NonSeekableReadStream(data);
        await using var cipher = new MemoryStream();

        await sut.Encrypt(plain, cipher);

        cipher.Position = 0;
        await using var decrypted = new MemoryStream();
        await sut.Decrypt(cipher, decrypted);
        decrypted.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task Encrypt_when_cancelled_propagates_cancellation()
    {
        var sut = CreateService();
        await using var plain = new MemoryStream(new byte[1024]);
        await using var cipher = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.Encrypt(plain, cipher, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 + 100)]
    public async Task ComputeCiphertextLength_matches_actual_encrypt_output(int plaintextLength)
    {
        var sut = CreateService();
        var data = new byte[plaintextLength];
        if (plaintextLength > 0)
        {
            RandomNumberGenerator.Fill(data);
        }

        await using var plain = new MemoryStream(data);
        await using var cipher = new MemoryStream();
        await sut.Encrypt(plain, cipher);

        sut.ComputeCiphertextLength(plaintextLength).Should().Be(cipher.Length);
    }

    [Fact]
    public async Task ComputeCiphertextLength_matches_output_when_stream_returns_short_reads()
    {
        var sut = CreateService();
        var data = new byte[2 * 1024 * 1024 + 100];
        RandomNumberGenerator.Fill(data);

        await using var plain = new ShortReadStream(data, maxReadBytes: 7 * 1024);
        await using var cipher = new MemoryStream();
        await sut.Encrypt(plain, cipher);

        sut.ComputeCiphertextLength(data.Length).Should().Be(cipher.Length);

        cipher.Position = 0;
        await using var decrypted = new MemoryStream();
        await sut.Decrypt(cipher, decrypted);
        decrypted.ToArray().Should().Equal(data);
    }

    [Fact]
    public void ComputeCiphertextLength_rejects_negative()
    {
        var sut = CreateService();
        var act = () => sut.ComputeCiphertextLength(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
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

    private sealed class ShortReadStream(byte[] data, int maxReadBytes) : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var boundedBuffer = buffer[..Math.Min(buffer.Length, maxReadBytes)];
            return base.ReadAsync(boundedBuffer, cancellationToken);
        }
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
