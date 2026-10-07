using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
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

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class RemoveAssetTagCommandHandlerTests
{
    private readonly IModerationFoundationStore _moderationStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly RemoveAssetTagCommandHandler _handler;
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _tagId = Guid.NewGuid();

    public RemoveAssetTagCommandHandlerTests()
    {
        _moderationStoreMock = Substitute.For<IModerationFoundationStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _moderationStoreMock.GetOwnerDraftSnapshot(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DraftSnapshot(["existing", "other"]));
        _moderationStoreMock.EnsurePreUploadWorkspace(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(WorkspaceSnapshot(3));
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.SUCCEEDED, 4, 4));

        _handler = new RemoveAssetTagCommandHandler(
            _moderationStoreMock,
            _tagStoreMock,
            _auditWriterMock,
            NullLogger<RemoveAssetTagCommandHandler>.Instance);
    }

    private static SellerDraftSnapshotDto DraftSnapshot(IReadOnlyList<string> tags) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            1,
            new SellerDraftMaterialPayload("Title", null, Guid.NewGuid(), tags),
            Declaration: null,
            DeclarationComplete: false,
            LatestVersionId: null,
            LatestVersionNumber: null);

    private static AssetDraftWorkspaceSnapshot WorkspaceSnapshot(long revision) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), revision, 1, 0, 0, 0);

    [Fact]
    public async Task Handle_WhenTagNotFound_ShouldReturnNotFound()
    {
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _moderationStoreMock.DidNotReceive().GetOwnerDraftSnapshot(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDraftSnapshotNotFound_ShouldReturnNotFound()
    {
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = _tagId, Name = "existing" });
        _moderationStoreMock.GetOwnerDraftSnapshot(_assetId, _userId, Arg.Any<CancellationToken>())
            .Returns((SellerDraftSnapshotDto?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenTagNotOnDraft_ShouldReturnNotFound()
    {
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = _tagId, Name = "missing" });

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_NOT_FOUND);
        await _moderationStoreMock.DidNotReceive().SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDraftRevisionIsStale_ShouldReturnConflict()
    {
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = _tagId, Name = "existing" });
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.STALE_WORKSPACE, 9));

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        await _auditWriterMock.DidNotReceive().WriteBestEffort(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagOnDraft_ShouldRemoveTagFromDraftRevision()
    {
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = _tagId, Name = "existing" });

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderationStoreMock.Received(1).SaveDraftRevision(
            Arg.Is<DraftRevisionSaveRequest>(r =>
                r.ActorUserId == _userId &&
                r.AssetId == _assetId &&
                r.AssetVersionId == null &&
                r.OperationKind == ModerationOperationKinds.DRAFT_SAVE &&
                r.ExpectedWorkspaceRevision == 3 &&
                !r.PayloadJson.Contains("existing") &&
                r.PayloadJson.Contains("other")),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_REMOVE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == _assetId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagOnDraft_ShouldNotResolveASecondWorkspaceBeforeCAS()
    {
        // CAS revision must come from the same snapshot as the removed-tag payload;
        // a later EnsurePreUploadWorkspace read could mask a concurrent material save.
        var command = new RemoveAssetTagCommand(_assetId, _userId, _tagId);
        _tagStoreMock.GetById(_tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = _tagId, Name = "existing" });

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderationStoreMock.DidNotReceive().EnsurePreUploadWorkspace(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
