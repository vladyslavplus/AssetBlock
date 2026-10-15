using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing;

/// <summary>
/// Strict variant of <see cref="DecryptedContentPipeline"/> for security and analysis admission.
/// A result is only produced when the whole plaintext was consumed, the decryptor completed the AEAD
/// end marker, and the observed plaintext hash matches what the consumer computed.
///
/// The permissive pipeline is not sufficient evidence of a complete verification: a consumer may
/// finish early, cancel the decryptor, and observe producer faults be suppressed.
/// </summary>
internal static class StrictDecryptedContentPipeline
{
    private const int PAUSE_WRITER_THRESHOLD_BYTES = 64 * 1024;
    private const int RESUME_WRITER_THRESHOLD_BYTES = 32 * 1024;

    public sealed record StrictDecryptedContent(long ByteCount, string ContentSha256);

    /// <summary>
    /// Observed result of one verified stream run. <see cref="FullyVerified"/> is true only when
    /// the consumer read the whole plaintext and the decryptor completed its AEAD end marker;
    /// only then is <see cref="Content"/> admissible proof. A consumer that returns early (a
    /// refusal, an operational error, or an early verdict) yields its result unverified: the lack
    /// of proof is never mistaken for proven corruption.
    /// </summary>
    public sealed record VerifiedRun<T>(T Result, StrictDecryptedContent? Content, bool FullyVerified);

    /// <summary>
    /// Runs <paramref name="consume"/> over the decrypted stream and returns the result with its
    /// verification state. Producer integrity failures (AEAD/truncation faults from the decryptor)
    /// always throw; a consumer early return preserves the consumer's own result as unverified.
    /// Consumer failures propagate unchanged and are never masked as integrity faults.
    /// </summary>
    public static async Task<VerifiedRun<T>> RunVerified<T>(
        Stream encryptedStream,
        IEncryptionService encryptionService,
        Func<Stream, CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryptedStream);
        ArgumentNullException.ThrowIfNull(encryptionService);
        ArgumentNullException.ThrowIfNull(consume);

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PAUSE_WRITER_THRESHOLD_BYTES,
            resumeWriterThreshold: RESUME_WRITER_THRESHOLD_BYTES,
            useSynchronizationContext: false));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var hashingReader = new HashingReader(pipe.Reader.AsStream(leaveOpen: true));
        Task producer = Produce(encryptedStream, encryptionService, pipe.Writer, linkedCts.Token);

        T result;
        try
        {
            result = await consume(hashingReader, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception consumeException)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync(consumeException).ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (CryptographicException producerException)
            {
                // Authenticated-input corruption is authoritative for security handlers,
                // even if the consumer also failed while the pipe was completing.
                ExceptionDispatchInfo.Capture(producerException).Throw();
                throw;
            }
            catch (OperationCanceledException producerException)
                when (linkedCts.IsCancellationRequested
                    && producerException.CancellationToken == linkedCts.Token
                    && (!cancellationToken.IsCancellationRequested
                        || consumeException is OperationCanceledException))
            {
                // Cleanup canceled a backpressured producer after the consumer failed.
            }
            catch (Exception producerException) when (ReferenceEquals(producerException, consumeException))
            {
                // Completing the reader can surface the consumer's same exception through a blocked writer.
            }
            catch (Exception producerException)
            {
                throw new AggregateException(consumeException, producerException);
            }

            throw;
        }

        // An early return leaves plaintext unread: the result stands on its own but carries no
        // verified proof. A genuine producer fault still takes precedence over the early return.
        if (!hashingReader.ReachedEndOfStream)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Our own cancellation after the consumer stopped reading.
            }

            return new VerifiedRun<T>(result, null, FullyVerified: false);
        }

        // The decryptor must have completed successfully, including the AEAD end marker. Only then is
        // the observed plaintext hash evidence of the complete, authenticated content.
        await producer.ConfigureAwait(false);
        await pipe.Reader.CompleteAsync().ConfigureAwait(false);

        var content = new StrictDecryptedContent(hashingReader.TotalBytesRead, hashingReader.ContentSha256);
        hashingReader.ResetHash();
        return new VerifiedRun<T>(result, content, FullyVerified: true);
    }

    /// <summary>Hashes every plaintext byte the consumer actually reads.</summary>
    private sealed class HashingReader(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long TotalBytesRead { get; private set; }

        public bool ReachedEndOfStream { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public string ContentSha256
        {
            get
            {
                Span<byte> digest = stackalloc byte[32];
                _hash.GetCurrentHash(digest);
                return Convert.ToHexString(digest).ToLowerInvariant();
            }
        }

        public void ResetHash() => _hash.GetHashAndReset();

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // A zero-length read says nothing about the stream, so it must not be taken as EOF.
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var read = inner.Read(buffer);
            if (read == 0)
            {
                ReachedEndOfStream = true;
                return 0;
            }

            _hash.AppendData(buffer[..read]);
            TotalBytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                ReachedEndOfStream = true;
                return 0;
            }

            _hash.AppendData(buffer.Span[..read]);
            TotalBytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static async Task Produce(
        Stream encryptedStream,
        IEncryptionService encryptionService,
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream writerStream = writer.AsStream(leaveOpen: true);
            await encryptionService.Decrypt(encryptedStream, writerStream, cancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
            throw;
        }
    }
}
