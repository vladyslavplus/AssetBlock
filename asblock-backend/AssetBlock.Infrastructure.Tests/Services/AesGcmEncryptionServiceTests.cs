using System.Security.Cryptography;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class AesGcmEncryptionServiceTests
{
    private static AesGcmEncryptionService CreateService()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string> { ["k1"] = key }
        }));
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
    public void Constructor_throws_when_keys_missing()
    {
        var act = () => new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>()
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one configured encryption key*");
    }

    [Fact]
    public void Constructor_throws_when_current_key_id_missing()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var act = () => new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "",
            Keys = new Dictionary<string, string> { ["k1"] = key }
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CurrentKeyId must be specified*");
    }

    [Fact]
    public void Constructor_throws_when_current_key_id_not_in_keys()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var act = () => new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k2",
            Keys = new Dictionary<string, string> { ["k1"] = key }
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CurrentKeyId 'k2' was not found in Encryption:Keys*");
    }

    [Theory]
    [InlineData(16)] // AES-128
    [InlineData(24)] // AES-192
    [InlineData(5)]  // Bad size
    public void Constructor_rejects_non_32_byte_keys(int keyBytesLength)
    {
        var act = () => new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = Convert.ToBase64String(new byte[keyBytesLength])
            }
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void Constructor_rejects_key_id_exceeding_max_bytes()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var longKeyId = new string('k', 65);
        var act = () => new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = longKeyId,
            Keys = new Dictionary<string, string>
            {
                [longKeyId] = key
            }
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeds maximum length of 64 bytes*");
    }

    [Fact]
    public async Task Decrypt_WhenHeaderlessOrInvalidMagic_ThrowsCryptographicException()
    {
        // Produce headerless unversioned ciphertext: [4-byte len][12-byte nonce][16-byte tag][ciphertext][4-byte EOS]
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var originalPlaintext = "Headerless unversioned encrypted content without ABE1 header"u8.ToArray();

        using var headerlessCipher = new MemoryStream();
        using (var aes = new AesGcm(keyBytes, 16))
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipherChunk = new byte[originalPlaintext.Length];
            var aad = BitConverter.GetBytes(0L);
            aes.Encrypt(nonce, originalPlaintext, cipherChunk, tag, aad);

            headerlessCipher.Write(BitConverter.GetBytes((uint)originalPlaintext.Length));
            headerlessCipher.Write(nonce);
            headerlessCipher.Write(tag);
            headerlessCipher.Write(cipherChunk);
            headerlessCipher.Write(BitConverter.GetBytes(uint.MaxValue)); // EOS
        }

        headerlessCipher.Position = 0;

        var sut = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = Convert.ToBase64String(keyBytes)
            }
        }));

        using var decrypted = new MemoryStream();
        var act = () => sut.Decrypt(headerlessCipher, decrypted);

        await act.Should().ThrowAsync<CryptographicException>()
            .WithMessage("*missing ABE1 header*");
    }

    [Fact]
    public async Task EncryptDecrypt_ParallelMultiChunkAcrossMultipleKeys_MatchesExactOutputAndCompletesWithoutFailures()
    {
        var key1Bytes = RandomNumberGenerator.GetBytes(32);
        var key2Bytes = RandomNumberGenerator.GetBytes(32);
        var key3Bytes = RandomNumberGenerator.GetBytes(32);

        var sut = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k2",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = Convert.ToBase64String(key1Bytes),
                ["k2"] = Convert.ToBase64String(key2Bytes),
                ["k3"] = Convert.ToBase64String(key3Bytes)
            }
        }));

        const int concurrency = 10;
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            // Plaintext spanning multiple chunks (e.g. 2.5 MB)
            var size = (2 * 1024 * 1024) + (i * 100 * 1024) + 1234;
            var plainData = new byte[size];
            RandomNumberGenerator.Fill(plainData);

            using var plainStream = new MemoryStream(plainData);
            using var cipherStream = new MemoryStream();

            await sut.Encrypt(plainStream, cipherStream);

            cipherStream.Position = 0;
            sut.ComputeCiphertextLength(size).Should().Be(cipherStream.Length);

            using var decryptedStream = new MemoryStream();
            await sut.Decrypt(cipherStream, decryptedStream);

            decryptedStream.ToArray().Should().Equal(plainData);
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task KeyRing_Rotation_EncryptsWithCurrentKey_AndDecryptsAcrossKeys()
    {
        var key1Bytes = RandomNumberGenerator.GetBytes(32);
        var key2Bytes = RandomNumberGenerator.GetBytes(32);
        var key1Base64 = Convert.ToBase64String(key1Bytes);
        var key2Base64 = Convert.ToBase64String(key2Bytes);

        // Service with key1 active
        var sutKey1 = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key1Base64,
                ["k2"] = key2Base64
            }
        }));

        // Service with key2 active (key rotated)
        var sutKey2 = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k2",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key1Base64,
                ["k2"] = key2Base64
            }
        }));

        var plainData1 = "Encrypted with Key 1"u8.ToArray();
        var plainData2 = "Encrypted with Key 2"u8.ToArray();

        using var cipher1 = new MemoryStream();
        await sutKey1.Encrypt(new MemoryStream(plainData1), cipher1);
        cipher1.Position = 0;

        using var cipher2 = new MemoryStream();
        await sutKey2.Encrypt(new MemoryStream(plainData2), cipher2);
        cipher2.Position = 0;

        // sutKey2 can decrypt stream encrypted with key1 because k1 is in its keyring
        using var decrypted1 = new MemoryStream();
        await sutKey2.Decrypt(cipher1, decrypted1);
        decrypted1.ToArray().Should().Equal(plainData1);

        // sutKey1 can decrypt stream encrypted with key2 because k2 is in its keyring
        using var decrypted2 = new MemoryStream();
        await sutKey1.Decrypt(cipher2, decrypted2);
        decrypted2.ToArray().Should().Equal(plainData2);
    }

    [Fact]
    public async Task Decrypt_WhenUnknownKeyId_FailsClosedWithCryptographicException()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var sutKey1 = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "unknown-key-id",
            Keys = new Dictionary<string, string>
            {
                ["unknown-key-id"] = Convert.ToBase64String(keyBytes)
            }
        }));

        using var cipher = new MemoryStream();
        await sutKey1.Encrypt(new MemoryStream("some plain"u8.ToArray()), cipher);
        cipher.Position = 0;

        // Consumer does not have unknown-key-id in keyring
        var consumerSut = new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));

        using var decrypted = new MemoryStream();
        var act = () => consumerSut.Decrypt(cipher, decrypted);

        await act.Should().ThrowAsync<CryptographicException>()
            .WithMessage("*Unknown encryption key ID*");
    }

    [Fact]
    public async Task Decrypt_WhenTruncatedStream_ThrowsCryptographicException()
    {
        var sut = CreateService();
        using var cipher = new MemoryStream();
        await sut.Encrypt(new MemoryStream("test data"u8.ToArray()), cipher);

        // Truncate stream before EOS marker
        var truncated = new MemoryStream(cipher.ToArray()[..^4]);
        using var decrypted = new MemoryStream();
        var act = () => sut.Decrypt(truncated, decrypted);

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Dispose_DisposesServiceAndThrowsOnSubsequentUse()
    {
        var sut = CreateService();
        sut.Dispose();

        var act = () => sut.Encrypt(new MemoryStream([1, 2, 3]), new MemoryStream());
        await act.Should().ThrowAsync<ObjectDisposedException>();
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
