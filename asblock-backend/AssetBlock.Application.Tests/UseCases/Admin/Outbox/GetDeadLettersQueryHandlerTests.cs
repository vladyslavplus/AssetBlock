using AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Paging;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Admin.Outbox;

public sealed class GetDeadLettersQueryHandlerTests
{
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly GetDeadLettersQueryHandler _handler;

    public GetDeadLettersQueryHandlerTests()
    {
        _handler = new GetDeadLettersQueryHandler(_outboxStore);
    }

    [Fact]
    public async Task Handle_WhenCalled_ShouldReturnPagedResultsFromStore()
    {
        var request = new GetDeadLettersRequest(1, 10);
        var query = new GetDeadLettersQuery(request);
        var expectedItems = new List<DeadLetterOutboxListItemDto>
        {
            new(Guid.NewGuid(), "EmailDispatch", DateTimeOffset.UtcNow.AddMinutes(-10), 10, DateTimeOffset.UtcNow.AddMinutes(-2), "SMTP connection refused", 0, null)
        };
        var expectedPagedResult = new PagedResult<DeadLetterOutboxListItemDto>(expectedItems, 1, 1, 10);

        _outboxStore.GetDeadLetters(request, Arg.Any<CancellationToken>())
            .Returns(expectedPagedResult);

        Ardalis.Result.Result<PagedResult<DeadLetterOutboxListItemDto>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expectedPagedResult);
        result.Value.Items.Should().HaveCount(1);
    }
}
