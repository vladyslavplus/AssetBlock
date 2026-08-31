using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Reviews.GetReviews;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class GetReviewsQueryHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly ITypedCache _cacheMock;
    private readonly GetReviewsQueryHandler _handler;

    public GetReviewsQueryHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _cacheMock = Substitute.For<ITypedCache>();

        _handler = new GetReviewsQueryHandler(
            _reviewStoreMock,
            _cacheMock,
            NullLogger<GetReviewsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCached_ShouldReturnFromCache()
    {
        var assetId = Guid.NewGuid();
        var request = new GetReviewsRequest { Page = 1, PageSize = 10, SortDirection = SortDirection.DESC };
        var query = new GetReviewsQuery(assetId, request);
        var key = CacheKeys.ReviewsList(assetId, request);

        var items = new List<ReviewListItem>
        {
            new(Guid.NewGuid(), assetId, Guid.NewGuid(), "user1", 5, "Good", DateTimeOffset.UtcNow)
        };
        var pagedResult = new PagedResult<ReviewListItem>(items, 1, 1, 10);

        _cacheMock.Get<PagedResult<ReviewListItem>>(key, Arg.Any<CancellationToken>()).Returns(pagedResult);

        Ardalis.Result.Result<PagedResult<ReviewListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Username.Should().Be("user1");

        await _reviewStoreMock.DidNotReceive().GetPaged(Arg.Any<Guid>(), Arg.Any<GetReviewsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotCached_ShouldReturnPagedResultAndCache()
    {
        var assetId = Guid.NewGuid();
        var request = new GetReviewsRequest { Page = 1, PageSize = 10, SortDirection = SortDirection.DESC };
        var query = new GetReviewsQuery(assetId, request);
        var key = CacheKeys.ReviewsList(assetId, request);

        var items = new List<ReviewListItem>
        {
            new(Guid.NewGuid(), assetId, Guid.NewGuid(), "alice", 4, "Nice", DateTimeOffset.UtcNow)
        };
        var pagedResult = new PagedResult<ReviewListItem>(items, 1, 1, 10);

        _cacheMock.Get<PagedResult<ReviewListItem>>(key, Arg.Any<CancellationToken>()).Returns((PagedResult<ReviewListItem>?)null);
        _reviewStoreMock.GetPaged(assetId, request, Arg.Any<CancellationToken>()).Returns(pagedResult);

        Ardalis.Result.Result<PagedResult<ReviewListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Username.Should().Be("alice");

        await _cacheMock.Received(1).Set(
            key,
            Arg.Is<PagedResult<ReviewListItem>>(r => r.Items[0].Username == "alice"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
