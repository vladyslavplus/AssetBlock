using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class DecryptedContentPipelineTests
{
    [Fact]
    public async Task Run_WhenProducerWritesChunks_ShouldNotRequireSeekableConsumerStream()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var destination = ci.ArgAt<Stream>(1);
                await destination.WriteAsync("abcdefghijklmnopqrstuvwxyz"u8.ToArray(), ci.Arg<CancellationToken>());
            });

        using var cipher = new MemoryStream([1, 2, 3]);
        var result = await DecryptedContentPipeline.Run(cipher, encryption, async (plain, ct) =>
        {
            plain.CanSeek.Should().BeFalse();
            using var reader = new StreamReader(plain);
            return await reader.ReadToEndAsync(ct);
        }, CancellationToken.None);

        result.Should().Be("abcdefghijklmnopqrstuvwxyz");
    }

    [Fact]
    public async Task Run_WhenConsumerFinishesEarly_ShouldCancelProducer()
    {
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                producerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ci.Arg<CancellationToken>());
                }
                catch (OperationCanceledException)
                {
                    producerCancelled.TrySetResult();
                    throw;
                }
            });

        using var cipher = new MemoryStream([1]);
        var result = await DecryptedContentPipeline.Run(cipher, encryption, (_, _) => Task.FromResult(7), CancellationToken.None);

        result.Should().Be(7);
        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await producerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_WhenProducerFails_ShouldSurfaceFailureToConsumer()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("decrypt exploded"));

        using var cipher = new MemoryStream([1]);
        var act = () => DecryptedContentPipeline.Run(cipher, encryption, async (plain, ct) =>
        {
            var buffer = new byte[16];
            return await plain.ReadAsync(buffer, ct);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Run_WhenConsumerFails_ShouldCancelProducerAndRethrow()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ci.Arg<CancellationToken>());
            });

        using var cipher = new MemoryStream([1]);
        var act = () => DecryptedContentPipeline.Run<int>(
            cipher,
            encryption,
            (_, _) => throw new InvalidOperationException("consumer exploded"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("consumer exploded");
    }

    [Fact]
    public async Task Run_WhenConsumerFinishesWhileProducerIsBlockedOnBackpressure_ShouldReturnConsumerResult()
    {
        var producerEnteredWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var destination = ci.ArgAt<Stream>(1);
                var token = ci.Arg<CancellationToken>();
                var chunk = new byte[32 * 1024];
                await destination.WriteAsync(chunk, token);
                producerEnteredWrite.TrySetResult();
                for (var i = 0; i < 8; i++)
                {
                    await destination.WriteAsync(chunk, token);
                    await destination.FlushAsync(token);
                }
            });

        using var cipher = new MemoryStream([1, 2, 3]);
        var result = await DecryptedContentPipeline.Run(cipher, encryption, async (_, _) =>
        {
            await producerEnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return 11;
        }, CancellationToken.None);

        result.Should().Be(11);
    }

    [Fact]
    public async Task Run_WhenCancelled_ShouldThrowWithoutHanging()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ci.Arg<CancellationToken>());
            });

        using var cipher = new MemoryStream([1]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => DecryptedContentPipeline.Run(cipher, encryption, async (plain, ct) =>
        {
            var buffer = new byte[16];
            while (!ct.IsCancellationRequested)
            {
                _ = await plain.ReadAsync(buffer, ct);
            }

            return 0;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
