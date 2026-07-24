using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class UpdateAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateAssetCommandHandler _handler;

    public UpdateAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            _categoryStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<UpdateAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_UPDATE
                && e.Outcome == AuditOutcome.DENIED
                && e.ResourceId == command.AssetId.ToString()),
            Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryIdProvidedAndNotFound_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, null, null, null, categoryId);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _categoryStoreMock.GetById(categoryId).Returns((Category?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithPartialUpdate_ShouldUpdateAndClearCache()
    {
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Updated Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_UPDATE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceId == command.AssetId.ToString()
                && e.Metadata != null),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUpdateReturnsFalse_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }
}
