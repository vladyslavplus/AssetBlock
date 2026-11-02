using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Moderation.RequestCoverageReview;

public sealed record RequestCoverageReviewCommand(Guid ActorId, Guid AssetId, Guid VersionId, CoverageReviewCreateRequest Input) : IRequest<Result<CoverageReviewSaved>>;
internal sealed class RequestCoverageReviewCommandValidator : AbstractValidator<RequestCoverageReviewCommand>
{
    public RequestCoverageReviewCommandValidator()
    {
        RuleFor(c => c.ActorId).NotEmpty();
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.VersionId).NotEmpty();
        RuleFor(c => c.Input.OperationId).NotEmpty();
        RuleFor(c => c.Input.WorkspaceId).NotEmpty();
        RuleFor(c => c.Input.ReportId).NotEmpty();
        RuleFor(c => c.Input.ExpectedWorkspaceRevision).GreaterThan(0);
    }
}
internal sealed class RequestCoverageReviewCommandHandler(IModerationFoundationStore store) : IRequestHandler<RequestCoverageReviewCommand, Result<CoverageReviewSaved>>
{
    public async Task<Result<CoverageReviewSaved>> Handle(RequestCoverageReviewCommand command, CancellationToken ct)
    {
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new { command.AssetId, command.VersionId, command.Input.WorkspaceId, command.Input.ReportId, command.Input.ExpectedWorkspaceRevision }));
        return CoverageReviewResults.Map(await store.RequestCoverageReview(command.ActorId, command.AssetId, command.VersionId, command.Input, digest, ct));
    }
}
