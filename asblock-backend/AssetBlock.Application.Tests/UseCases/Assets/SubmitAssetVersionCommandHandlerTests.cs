using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class SubmitAssetVersionCommandHandlerTests
{
    [Theory]
    [InlineData(ErrorCodes.ERR_MODERATION_APPEAL_REQUIRED)]
    [InlineData(ErrorCodes.ERR_MODERATION_REJECTION_FINAL)]
    public async Task Handle_WhenRejectionBlocksSubmission_ShouldReturnConflict(string code)
    {
        IModerationFoundationStore moderation = Substitute.For<IModerationFoundationStore>();
        ICodeAnalysisSellerStore analysis = Substitute.For<ICodeAnalysisSellerStore>();
        var request = new SubmitAssetVersionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 2, Guid.NewGuid());
        moderation.AttemptGuardedSubmission(Arg.Any<GuardedSubmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GuardedSubmissionResult(GuardedSubmissionStatus.BLOCKED, null, code));
        Result<Guid> result = await new SubmitAssetVersionCommandHandler(moderation, analysis).Handle(request, CancellationToken.None);
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().ContainSingle().Which.Should().Be(code);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_WhenOwnerSubmits_ShouldBindOnlyServerSelectedCurrentReport(bool current)
    {
        IModerationFoundationStore moderation = Substitute.For<IModerationFoundationStore>();
        ICodeAnalysisSellerStore analysis = Substitute.For<ICodeAnalysisSellerStore>();
        var request = new SubmitAssetVersionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 2, Guid.NewGuid());
        var reportId = Guid.NewGuid();
        analysis.GetOwnerSummary(request.AssetVersionId, request.OwnerId, Arg.Any<CancellationToken>()).Returns(
            new SellerAnalysisSummaryDto(request.AssetId, request.AssetVersionId, SellerAnalysisLifecycle.COMPLETED,
                Guid.NewGuid(), reportId, current, CodeAnalysisCoverageLevel.FULL, 1, 0, 0, true,
                null, null, null, null, null, false, null, current, current, "Selected files", [], []));
        var submissionId = Guid.NewGuid();
        moderation.AttemptGuardedSubmission(Arg.Any<GuardedSubmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GuardedSubmissionResult(GuardedSubmissionStatus.REPLAYED, submissionId, null));
        var handler = new SubmitAssetVersionCommandHandler(moderation, analysis);
        Result<Guid> result = await handler.Handle(request, CancellationToken.None);
        result.Value.Should().Be(submissionId);
        await moderation.Received(1).AttemptGuardedSubmission(Arg.Is<GuardedSubmissionRequest>(r =>
            r.CodeAnalysisReportHeaderId == (current ? reportId : null) && r.RequestDigest.Length == 64), Arg.Any<CancellationToken>());
    }
}
