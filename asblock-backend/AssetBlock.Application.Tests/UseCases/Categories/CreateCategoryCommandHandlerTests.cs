using AssetBlock.Application.UseCases.Categories.CreateCategory;
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

public class CreateCategoryCommandHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly CreateCategoryCommandHandler _handler;

    public CreateCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<CreateCategoryCommandHandler> loggerMock = NullLogger<CreateCategoryCommandHandler>.Instance;

        _handler = new CreateCategoryCommandHandler(_categoryStoreMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WithExistingSlug_ShouldReturnError()
    {
        // Arrange
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);

        await _categoryStoreMock.DidNotReceiveWithAnyArgs().Create(null!, null, null!);
    }

    [Fact]
    public async Task Handle_WhenDatabaseThrowsDuplicateSlugException_ShouldReturnError()
    {
        // Arrange
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Create(command.Name, command.Description, command.Slug, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateSlugException());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
    }

    [Fact]
    public async Task Handle_WithValidCommand_ShouldReturnSuccessAndClearCache()
    {
        // Arrange
        var command = new CreateCategoryCommand("Test", "Desc", "test-slug");
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Test", Slug = "test-slug" };

        _categoryStoreMock.SlugExists(command.Slug, null, Arg.Any<CancellationToken>()).Returns(false);
        _categoryStoreMock.Create(command.Name, command.Description, command.Slug, Arg.Any<CancellationToken>())
            .Returns(category);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(categoryId);

        // Should invoke cache invalidation
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
