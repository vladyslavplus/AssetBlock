using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationUnread;

public sealed record MarkNotificationUnreadCommand(Guid UserId, Guid NotificationId) : IRequest<Result>;
