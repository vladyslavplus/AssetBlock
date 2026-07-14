using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Infrastructure.Outbox;

public interface IOutboxMessageHandler
{
    string MessageType { get; }
    Task Handle(OutboxMessage message, CancellationToken cancellationToken);
}
