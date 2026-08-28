using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Outbox;

namespace AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;

public sealed record ReplayDeadLetterCommand(Guid Id) : IRequest<Result<ReplayDeadLetterResponseDto>>;
