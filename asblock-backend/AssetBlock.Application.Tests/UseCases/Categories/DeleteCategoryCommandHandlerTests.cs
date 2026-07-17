using AssetBlock.Application.UseCases.Categories.DeleteCategory;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
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
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteCategoryCommandHandler _handler;

    public DeleteCategoryCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();
        ILogger<DeleteCategoryCommandHandler> loggerMock = NullLogger<DeleteCategoryCommandHandler>.Instance;

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new DeleteCategoryCommandHandler(_categoryStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, loggerMock);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _cacheMock.DidNotReceive().RemoveByPrefix(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryInUse_ShouldReturnBadRequest()
    {
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new CategoryInUseException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_BAD_REQUEST);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldWriteAuditClearCacheAndReturnSuccess()
    {
        var command = new DeleteCategoryCommand(Guid.NewGuid());
        _categoryStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.CATEGORY_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == command.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }
}
