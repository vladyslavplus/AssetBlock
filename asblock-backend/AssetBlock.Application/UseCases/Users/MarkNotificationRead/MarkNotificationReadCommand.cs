using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationRead;

public sealed record MarkNotificationReadCommand(Guid UserId, Guid NotificationId) : IRequest<Result>;
