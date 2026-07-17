using Ardalis.Result;
using AssetBlock.Application.UseCases.Categories.UpdateCategory;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Categories;

public class UpdateCategoryCommandHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateCategoryCommandHandler _handler;

    public UpdateCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<UpdateCategoryCommandHandler> loggerMock = NullLogger<UpdateCategoryCommandHandler>.Instance;

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateCategoryCommandHandler(_categoryStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), "New Name", null, null);
        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns((Category?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenSlugExistsForAnotherCategory_ShouldReturnConflict()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, "existing-slug");
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);
        _categoryStoreMock.SlugExists(command.Slug!, command.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenDatabaseThrowsDuplicateSlug_ShouldReturnConflict()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, "new-slug");
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);
        _categoryStoreMock.SlugExists(command.Slug!, command.Id, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Update(Arg.Any<Category>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DuplicateSlugException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WithValidPartialUpdate_ShouldUpdateFieldsWriteAuditAndClearCache()
    {
        var command = new UpdateCategoryCommand(Guid.NewGuid(), "Updated Name", null, null);
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Description = "Old Desc", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        existingCategory.Name.Should().Be("Updated Name");
        existingCategory.Description.Should().Be("Old Desc");
        existingCategory.Slug.Should().Be("old-slug");

        await _categoryStoreMock.Received(1).Update(existingCategory, Arg.Any<CancellationToken>());
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.CATEGORY_UPDATE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == command.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
