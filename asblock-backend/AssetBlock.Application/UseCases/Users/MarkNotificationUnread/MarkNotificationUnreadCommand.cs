using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationUnread;

public sealed record MarkNotificationUnreadCommand(Guid UserId, Guid NotificationId) : IRequest<Result>;
