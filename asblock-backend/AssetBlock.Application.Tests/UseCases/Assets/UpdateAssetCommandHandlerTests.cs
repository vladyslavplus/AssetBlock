using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class UpdateAssetCommandHandlerTests
{
    private readonly IModerationFoundationStore _moderationStoreMock;
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly UpdateAssetCommandHandler _handler;
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _categoryId = Guid.NewGuid();

    public UpdateAssetCommandHandlerTests()
    {
        _moderationStoreMock = Substitute.For<IModerationFoundationStore>();
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _moderationStoreMock.EnsurePreUploadWorkspace(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(WorkspaceSnapshot(3));
        _moderationStoreMock.GetOwnerDraftSnapshot(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DraftSnapshot());
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.SUCCEEDED, 4, 4));

        _handler = new UpdateAssetCommandHandler(
            _moderationStoreMock,
            _categoryStoreMock,
            _auditWriterMock,
            NullLogger<UpdateAssetCommandHandler>.Instance);
    }

    private SellerDraftSnapshotDto DraftSnapshot() =>
        new(
            _assetId,
            Guid.NewGuid(),
            3,
            1,
            new SellerDraftMaterialPayload("Old Title", "Old Desc", _categoryId, ["tag1"]),
            Declaration: null,
            DeclarationComplete: false,
            LatestVersionId: null,
            LatestVersionNumber: null);

    private static AssetDraftWorkspaceSnapshot WorkspaceSnapshot(long revision) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), revision, 1, 0, 0, 0);

    [Fact]
    public async Task Handle_WhenPriceIsProvided_ShouldReturnConflict()
    {
        var command = new UpdateAssetCommand(_assetId, _userId, "New Title", null, 25m, null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_PRICE_OPERATION_ONLY);
        await _moderationStoreMock.DidNotReceive().EnsurePreUploadWorkspace(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenWorkspaceLockFails_ShouldReturnForbiddenAndWriteDeniedAudit()
    {
        var command = new UpdateAssetCommand(_assetId, _userId, "New Title", null, null, null);
        _moderationStoreMock.EnsurePreUploadWorkspace(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("locked by another owner"));

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_UPDATE &&
                e.Outcome == AuditOutcome.DENIED &&
                e.ResourceId == _assetId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryProvidedAndNotFound_ShouldReturnNotFound()
    {
        var categoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(_assetId, _userId, null, null, null, categoryId);
        _categoryStoreMock.GetById(categoryId, Arg.Any<CancellationToken>()).Returns((Category?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _moderationStoreMock.DidNotReceive().SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDraftSnapshotNotFound_ShouldReturnNotFound()
    {
        var command = new UpdateAssetCommand(_assetId, _userId, "New Title", null, null, null);
        _moderationStoreMock.GetOwnerDraftSnapshot(_assetId, _userId, Arg.Any<CancellationToken>())
            .Returns((SellerDraftSnapshotDto?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenDraftRevisionIsStale_ShouldReturnConflict()
    {
        var command = new UpdateAssetCommand(_assetId, _userId, "New Title", null, null, null);
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.STALE_WORKSPACE, 9));

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
    }

    [Fact]
    public async Task Handle_WithPartialUpdate_ShouldMergeFieldsIntoDraftRevision()
    {
        var newCategoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(_assetId, _userId, "Updated Title", null, null, newCategoryId);
        _categoryStoreMock.GetById(newCategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = newCategoryId, Name = "Cat", Slug = "cat" });

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderationStoreMock.Received(1).SaveDraftRevision(
            Arg.Is<DraftRevisionSaveRequest>(r =>
                r.ActorUserId == _userId &&
                r.AssetId == _assetId &&
                r.AssetVersionId == null &&
                r.OperationKind == ModerationOperationKinds.DRAFT_SAVE &&
                r.ExpectedWorkspaceRevision == 3 &&
                r.PayloadJson.Contains("Updated Title") &&
                r.PayloadJson.Contains("Old Desc") &&
                !r.PayloadJson.Contains("Old Title")),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_DRAFT_SAVE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == _assetId.ToString()),
            Arg.Any<CancellationToken>());
    }
}
