using AssetBlock.Application.UseCases.Categories.GetCategoryById;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Categories;

public class GetCategoryByIdQueryHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly GetCategoryByIdQueryHandler _handler;

    public GetCategoryByIdQueryHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        ILogger<GetCategoryByIdQueryHandler> loggerMock = NullLogger<GetCategoryByIdQueryHandler>.Instance;

        _handler = new GetCategoryByIdQueryHandler(_categoryStoreMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnError()
    {
        // Arrange
        var query = new GetCategoryByIdQuery(Guid.NewGuid());
        _categoryStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns((Category?)null);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenCategoryFound_ShouldReturnMappedResponse()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Found", Slug = "found-slug", Description = "Desc" };
        var query = new GetCategoryByIdQuery(categoryId);
        _categoryStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns(category);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(categoryId);
        result.Value.Name.Should().Be("Found");
        result.Value.Slug.Should().Be("found-slug");
        result.Value.Description.Should().Be("Desc");
    }

    [Fact]
    public async Task Handle_WhenStoreThrows_ShouldPropagate()
    {
        var query = new GetCategoryByIdQuery(Guid.NewGuid());
        _categoryStoreMock.GetById(query.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        var act = () => _handler.Handle(query, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
