using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class OutboxDispatcherTests
{
    [Fact]
    public async Task DispatchBatch_WhenDependencyFails_ShouldRecordRetryWithoutProcessingMessage()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.external",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 1
        };
        var outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkFailed(
                message.Id,
                lockToken,
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dependency unavailable"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);
        var startedAt = DateTimeOffset.UtcNow;

        await dispatcher.DispatchBatch(CancellationToken.None);

        await outbox.Received(1).MarkFailed(
            message.Id,
            lockToken,
            "dependency unavailable",
            Arg.Is<DateTimeOffset>(next => next > startedAt),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkProcessed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }
}
