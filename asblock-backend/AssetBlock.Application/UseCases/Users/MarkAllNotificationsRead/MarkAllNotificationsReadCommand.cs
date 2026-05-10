using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.MarkAllNotificationsRead;

public sealed record MarkAllNotificationsReadCommand(Guid UserId) : IRequest<Result<MarkAllNotificationsReadResponseDto>>;
