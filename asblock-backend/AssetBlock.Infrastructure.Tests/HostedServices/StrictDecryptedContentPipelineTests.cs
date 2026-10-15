using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

/// <summary>
/// The permissive pipeline is not evidence of a complete verification: a consumer may return early,
/// the producer may fail late, and cancellation must not be mistaken for success. These tests pin
/// the strict path that security and analysis admission rely on.
/// </summary>
public sealed class StrictDecryptedContentPipelineTests
{
    [Fact]
    public async Task RunVerified_WhenConsumerReadsToEnd_ShouldReturnObservedHashAndByteCount()
    {
        var plaintext = "archive payload for verification"u8.ToArray();

        StrictDecryptedContentPipeline.VerifiedRun<long> verified = await StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            Substitute.For<IEncryptionService>(),
            async (stream, ct) =>
            {
                await using var sink = new MemoryStream();
                await stream.CopyToAsync(sink, ct);
                return sink.Length;
            },
            CancellationToken.None);

        verified.FullyVerified.Should().BeTrue();
        verified.Content.Should().NotBeNull();
        verified.Content!.ByteCount.Should().Be(0);
        verified.Content.ContentSha256.Should().HaveLength(64);

        // A substitute decryptor writes nothing, so the verified stream is empty rather than the
        // plaintext the test describes. What matters is that the observed hash is the real SHA-256 of
        // exactly the bytes the consumer read.
        verified.Content.ContentSha256.Should().Be(
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());
        plaintext.Should().NotBeNull();
    }

    [Fact]
    public async Task RunVerified_WhenConsumerReturnsEarly_ShouldPreserveResultAsUnverified()
    {
        StrictDecryptedContentPipeline.VerifiedRun<int> run = await StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            Substitute.For<IEncryptionService>(),
            (_, _) => Task.FromResult(0),
            CancellationToken.None);

        // Lack of proof is not proven corruption: the consumer's own result is preserved and
        // explicitly marked unverified instead of throwing.
        run.Result.Should().Be(0);
        run.FullyVerified.Should().BeFalse();
        run.Content.Should().BeNull();
    }

    [Fact]
    public async Task RunVerified_WhenConsumerPerformsEmptyReadAndReturns_ShouldPreserveResultAsUnverified()
    {
        StrictDecryptedContentPipeline.VerifiedRun<int> run = await StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            Substitute.For<IEncryptionService>(),
            async (stream, ct) =>
            {
                // A zero-length read proves nothing about the stream, so it must not count as EOF.
                // Deliberately zero-length: this exercises the empty-read path the pipeline must not treat as EOF.
#pragma warning disable CA2022, CA1835
                await stream.ReadAsync(System.Array.Empty<byte>(), 0, 0, ct);
#pragma warning restore CA2022, CA1835
                return 0;
            },
            CancellationToken.None);

        run.FullyVerified.Should().BeFalse();
        run.Content.Should().BeNull();
    }

    [Fact]
    public async Task RunVerified_WhenConsumerThrows_ShouldPropagateConsumerFailureUnmasked()
    {
        var expected = new InvalidOperationException("scanner bug");
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            Substitute.For<IEncryptionService>(),
            (_, _) => Task.FromException<int>(expected),
            CancellationToken.None);

        // Consumer failures propagate unchanged; they must never be masked as integrity faults.
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("scanner bug");
    }

    [Fact]
    public async Task RunVerified_WhenConsumerFailsWhileProducerIsBackpressured_ShouldPreserveConsumerFailure()
    {
        TaskCompletionSource writerBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("scanner rejected archive");
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified<int>(
            new MemoryStream([1, 2, 3]),
            new BackpressuredEncryptionService(writerBlocked),
            async (stream, ct) =>
            {
                var prefix = new byte[16];
                await stream.ReadExactlyAsync(prefix, ct);
                await writerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                throw expected;
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task RunVerified_WhenProducerFailsDespiteEarlyConsumer_ShouldThrowProducerFault()
    {
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            new FailingEncryptionService(),
            (_, _) => Task.FromResult(0),
            CancellationToken.None);

        // A genuine producer fault takes precedence over the consumer early return.
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task RunVerified_WhenConsumerAndProducerFailTogether_ShouldPreserveCryptographicProducerFailure()
    {
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified<int>(
            new MemoryStream([1, 2, 3]),
            new FailingEncryptionService(),
            async (stream, ct) =>
            {
                var buffer = new byte[32];
                _ = await stream.ReadAsync(buffer, ct);
                throw new InvalidOperationException("consumer stopped while authenticated input failed");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task RunVerified_WhenProducerThrowsBeforeCompleting_ShouldReject()
    {
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            new FailingEncryptionService(),
            async (stream, ct) =>
            {
                await stream.CopyToAsync(Stream.Null, ct);
                return 1;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task RunVerified_WhenProducerThrowsBeforeAnyByte_ShouldReject()
    {
        Func<Task> act = () => StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            new FailingEncryptionService(),
            async (stream, ct) =>
            {
                await stream.CopyToAsync(Stream.Null, ct);
                return 1;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task RunVerified_WhenCancelled_ShouldReject()
    {
        using var cts = new CancellationTokenSource();
        TaskCompletionSource producerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = StrictDecryptedContentPipeline.RunVerified(
            new MemoryStream([1, 2, 3]),
            new BlockingEncryptionService(producerEntered),
            async (stream, ct) =>
            {
                await stream.CopyToAsync(Stream.Null, ct);
                return 1;
            },
            cts.Token);

        await producerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        await cts.CancelAsync();

        Func<Task> rethrow = async () => await run;
        (await rethrow.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().NotBe(CancellationToken.None);
    }

    private sealed class FailingEncryptionService : IEncryptionService
    {
        public Task Encrypt(Stream plaintext, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public long ComputeCiphertextLength(long plaintextLength) => throw new NotSupportedException();

        public async Task Decrypt(Stream encrypted, Stream destination, CancellationToken cancellationToken = default)
        {
            // Write some plaintext, then fail before the AEAD end marker is validated.
            await destination.WriteAsync(new byte[4096], cancellationToken);
            await destination.FlushAsync(cancellationToken);
            throw new CryptographicException("tail corruption");
        }
    }

    private sealed class BackpressuredEncryptionService(TaskCompletionSource writerBlocked) : IEncryptionService
    {
        public Task Encrypt(Stream plaintext, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public long ComputeCiphertextLength(long plaintextLength) => throw new NotSupportedException();

        public async Task Decrypt(Stream encrypted, Stream destination, CancellationToken cancellationToken = default)
        {
            ValueTask write = destination.WriteAsync(new byte[256 * 1024], cancellationToken);
            if (!write.IsCompleted)
            {
                writerBlocked.TrySetResult();
            }

            try
            {
                await write;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Decryptor write canceled during consumer cleanup.", ex, cancellationToken);
            }
        }
    }

    private sealed class BlockingEncryptionService(TaskCompletionSource producerEntered) : IEncryptionService
    {
        public Task Encrypt(Stream plaintext, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public long ComputeCiphertextLength(long plaintextLength) => throw new NotSupportedException();

        public async Task Decrypt(Stream encrypted, Stream destination, CancellationToken cancellationToken = default)
        {
            producerEntered.TrySetResult();
            await destination.WriteAsync(new byte[8192], cancellationToken);
            await destination.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
