using System.IO.Pipelines;
using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing;

/// <summary>
/// Decrypts an encrypted storage stream into a bounded Pipe so consumers never buffer the full plaintext.
/// </summary>
internal static class DecryptedContentPipeline
{
    private const int PAUSE_WRITER_THRESHOLD_BYTES = 64 * 1024;
    private const int RESUME_WRITER_THRESHOLD_BYTES = 32 * 1024;

    public static async Task<T> Run<T>(
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
        var producer = Produce(encryptedStream, encryptionService, pipe.Writer, linkedCts.Token);

        try
        {
            await using var readerStream = pipe.Reader.AsStream(leaveOpen: true);
            var result = await consume(readerStream, linkedCts.Token).ConfigureAwait(false);
            await linkedCts.CancelAsync();
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await WaitProducer(producer, ignoreNonCancelFaults: true).ConfigureAwait(false);
            return result;
        }
        catch (Exception consumeException)
        {
            await linkedCts.CancelAsync();
            await pipe.Reader.CompleteAsync(consumeException).ConfigureAwait(false);
            try
            {
                await WaitProducer(producer, ignoreNonCancelFaults: true).ConfigureAwait(false);
            }
            catch (Exception producerException) when (producerException is not OperationCanceledException)
            {
                throw new AggregateException(consumeException, producerException);
            }

            throw;
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
            await using var writerStream = writer.AsStream(leaveOpen: true);
            await encryptionService.Decrypt(encryptedStream, writerStream, cancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WaitProducer(Task producer, bool ignoreNonCancelFaults)
    {
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Consumer finished or failed and cancelled the decryptor.
        }
        catch (Exception) when (ignoreNonCancelFaults)
        {
            // After cancel, a blocked Write/Flush may observe a closed pipe instead of cancellation.
        }
    }
}
