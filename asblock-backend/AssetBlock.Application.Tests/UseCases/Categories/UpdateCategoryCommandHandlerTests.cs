using Ardalis.Result;
using AssetBlock.Application.UseCases.Categories.UpdateCategory;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
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
    private readonly ICacheService _cacheMock;
    private readonly UpdateCategoryCommandHandler _handler;

    public UpdateCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<UpdateCategoryCommandHandler> loggerMock = NullLogger<UpdateCategoryCommandHandler>.Instance;

        _handler = new UpdateCategoryCommandHandler(_categoryStoreMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var command = new UpdateCategoryCommand(Guid.NewGuid(), "New Name", null, null);
        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns((Category?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenSlugExistsForAnotherCategory_ShouldReturnError()
    {
        // Arrange
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, "existing-slug");
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);
        _categoryStoreMock.SlugExists(command.Slug!, command.Id, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenDatabaseThrowsDuplicateSlug_ShouldReturnError()
    {
         // Arrange
        var command = new UpdateCategoryCommand(Guid.NewGuid(), null, null, "new-slug");
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);
        _categoryStoreMock.SlugExists(command.Slug!, command.Id, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Update(Arg.Any<Category>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DuplicateSlugException());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WithValidPartialUpdate_ShouldUpdateFieldsAndClearCache()
    {
        // Arrange
        var command = new UpdateCategoryCommand(Guid.NewGuid(), "Updated Name", null, null);
        var existingCategory = new Category { Id = command.Id, Name = "Old Name", Description = "Old Desc", Slug = "old-slug" };

        _categoryStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(existingCategory);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Ensure only Name was updated, Description/Slug remained unchanged
        existingCategory.Name.Should().Be("Updated Name");
        existingCategory.Description.Should().Be("Old Desc");
        existingCategory.Slug.Should().Be("old-slug");

        await _categoryStoreMock.Received(1).Update(existingCategory, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
