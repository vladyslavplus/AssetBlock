using Ardalis.Result;
using AssetBlock.Application.UseCases.Categories.CreateCategory;
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

public class CreateCategoryCommandHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly CreateCategoryCommandHandler _handler;

    public CreateCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<CreateCategoryCommandHandler> loggerMock = NullLogger<CreateCategoryCommandHandler>.Instance;

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new CreateCategoryCommandHandler(_categoryStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WithExistingSlug_ShouldReturnError()
    {
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        await _categoryStoreMock.DidNotReceiveWithAnyArgs().Create(null!, null, null!);
    }

    [Fact]
    public async Task Handle_WhenDatabaseThrowsDuplicateSlugException_ShouldReturnConflict()
    {
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Create(command.Name, command.Description, command.Slug, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateSlugException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WithValidCommand_ShouldReturnSuccessWriteAuditAndClearCache()
    {
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Test", Slug = "test-slug" };

        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Create(command.Name, command.Description, command.Slug, Arg.Any<CancellationToken>())
            .Returns(category);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(categoryId);
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.CATEGORY_CREATE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == categoryId.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
