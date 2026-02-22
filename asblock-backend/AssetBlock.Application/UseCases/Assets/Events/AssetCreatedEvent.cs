using MediatR;

namespace AssetBlock.Application.UseCases.Assets.Events;

public sealed record AssetCreatedEvent(Guid AssetId) : INotification;
