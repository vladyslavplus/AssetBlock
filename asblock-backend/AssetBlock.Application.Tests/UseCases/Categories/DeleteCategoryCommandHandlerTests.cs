using AssetBlock.Application.UseCases.Categories.DeleteCategory;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Categories;

public class DeleteCategoryCommandHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteCategoryCommandHandler _handler;

    public DeleteCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<DeleteCategoryCommandHandler> loggerMock = NullLogger<DeleteCategoryCommandHandler>.Instance;

        _handler = new DeleteCategoryCommandHandler(_categoryStoreMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _cacheMock.DidNotReceive().RemoveByPrefix(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryInUse_ShouldReturnBadRequest()
    {
        // Arrange
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new CategoryInUseException());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_BAD_REQUEST);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldClearCacheAndReturnSuccess()
    {
        // Arrange
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
