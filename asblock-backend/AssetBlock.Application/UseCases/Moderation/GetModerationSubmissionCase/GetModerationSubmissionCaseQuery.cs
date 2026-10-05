using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Moderation;

namespace AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;

public sealed record GetModerationSubmissionCaseQuery(Guid ActorUserId, Guid SubmissionId)
    : IRequest<Result<ModerationCaseSummary>>;
