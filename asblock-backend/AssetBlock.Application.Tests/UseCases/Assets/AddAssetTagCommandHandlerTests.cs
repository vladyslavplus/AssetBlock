using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class AddAssetTagCommandHandlerTests
{
    private readonly IModerationFoundationStore _moderationStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly AddAssetTagCommandHandler _handler;
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public AddAssetTagCommandHandlerTests()
    {
        _moderationStoreMock = Substitute.For<IModerationFoundationStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _moderationStoreMock.GetOwnerDraftSnapshot(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DraftSnapshot(["existing"]));
        _moderationStoreMock.EnsurePreUploadWorkspace(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(WorkspaceSnapshot(3));
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.SUCCEEDED, 4, 4));

        _handler = new AddAssetTagCommandHandler(
            _moderationStoreMock,
            _tagStoreMock,
            _auditWriterMock,
            NullLogger<AddAssetTagCommandHandler>.Instance);
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
    public async Task Handle_WhenDraftSnapshotNotFound_ShouldReturnNotFound()
    {
        var command = new AddAssetTagCommand(_assetId, _userId, "test");
        _moderationStoreMock.GetOwnerDraftSnapshot(_assetId, _userId, Arg.Any<CancellationToken>())
            .Returns((SellerDraftSnapshotDto?)null);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().GetByName(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagDoesNotExist_ShouldReturnNotFound()
    {
        var command = new AddAssetTagCommand(_assetId, _userId, " New-Tag ");
        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns((Tag?)null);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _moderationStoreMock.DidNotReceive().SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDraftRevisionIsStale_ShouldReturnConflict()
    {
        var command = new AddAssetTagCommand(_assetId, _userId, "new-tag");
        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "new-tag" });
        _moderationStoreMock.SaveDraftRevision(Arg.Any<DraftRevisionSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDraftSaveResult(ModerationDraftSaveStatus.STALE_WORKSPACE, 9));

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        await _auditWriterMock.DidNotReceive().WriteBestEffort(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagExists_ShouldAppendNormalizedTagToDraftRevision()
    {
        var command = new AddAssetTagCommand(_assetId, _userId, " New-Tag ");
        var tag = new Tag { Id = Guid.NewGuid(), Name = "new-tag" };
        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns(tag);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("new-tag");
        await _moderationStoreMock.Received(1).SaveDraftRevision(
            Arg.Is<DraftRevisionSaveRequest>(r =>
                r.ActorUserId == _userId &&
                r.AssetId == _assetId &&
                r.AssetVersionId == null &&
                r.OperationKind == ModerationOperationKinds.DRAFT_SAVE &&
                r.ExpectedWorkspaceRevision == 3 &&
                r.PayloadJson.Contains("new-tag")),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_ADD &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == _assetId.ToString() &&
                e.Metadata != null && e.Metadata.ContainsKey("tagId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagExists_ShouldNotResolveASecondWorkspaceBeforeCAS()
    {
        // The CAS revision must come from the same snapshot as the payload; a second
        // EnsurePreUploadWorkspace read could return a newer revision and silently
        // overwrite a concurrent material save.
        var command = new AddAssetTagCommand(_assetId, _userId, "new-tag");
        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "new-tag" });

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderationStoreMock.DidNotReceive().EnsurePreUploadWorkspace(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
