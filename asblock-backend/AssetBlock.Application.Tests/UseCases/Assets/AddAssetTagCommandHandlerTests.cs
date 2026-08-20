using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class AddAssetTagCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly AddAssetTagCommandHandler _handler;

    public AddAssetTagCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        var cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new AddAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            _unitOfWorkMock,
            _auditWriterMock,
            cacheMock,
            NullLogger<AddAssetTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbiddenAndWriteDeniedAudit()
    {
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", AssetTags = [] };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_ADD &&
                e.Outcome == AuditOutcome.DENIED &&
                e.ResourceId == asset.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagDoesNotExist_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, " New-Tag ");
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", AssetTags = [] };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("new-tag").Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.DidNotReceive().AddTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenTagAlreadyOnAsset_ShouldReturnConflict()
    {
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = Guid.NewGuid(),
            Title = "t",
            AssetTags = [new AssetTag { AssetId = assetId, TagId = tag.Id }]
        };
        var command = new AddAssetTagCommand(assetId, authorId, "existing");

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("existing").Returns(tag);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS);
        await _assetStoreMock.DidNotReceive().AddTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenTagExists_ShouldAddTagLinkAndWriteAuditInsideTransaction()
    {
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", AssetTags = [] };
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("existing").Returns(tag);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.Received(1).AddTag(command.AssetId, tag.Id);
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_ADD &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.Metadata != null && e.Metadata.ContainsKey("tagId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAddTagThrows_ShouldReturnError()
    {
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", AssetTags = [] };
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("existing").Returns(tag);
        _assetStoreMock.AddTag(command.AssetId, tag.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Error);
    }
}
