using System.Text.Json;
using AssetBlock.Application.UseCases.Reviews.GetReviews;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class GetReviewsQueryHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly GetReviewsQueryHandler _handler;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GetReviewsQueryHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _cacheMock = Substitute.For<ICacheService>();

        _handler = new GetReviewsQueryHandler(
            _reviewStoreMock,
            _cacheMock,
            NullLogger<GetReviewsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCached_ShouldReturnFromCache()
    {
        // Arrange
        var assetId = Guid.NewGuid();
        var request = new GetReviewsRequest { Page = 1, PageSize = 10, SortDirection = SortDirection.DESC };
        var query = new GetReviewsQuery(assetId, request);
        var key = CacheKeys.ReviewsList(assetId, request);

        var items = new List<ReviewListItem>
        {
            new(Guid.NewGuid(), assetId, Guid.NewGuid(), "user1", 5, "Good", DateTimeOffset.UtcNow)
        };
        var pagedResult = new PagedResult<ReviewListItem>(items, 1, 1, 10);
        var cachedJson = JsonSerializer.Serialize(pagedResult, _jsonOptions);

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns(cachedJson);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Username.Should().Be("user1");

        await _reviewStoreMock.DidNotReceive().GetPaged(Arg.Any<Guid>(), Arg.Any<GetReviewsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotCached_ShouldReturnPagedResultAndCache()
    {
        // Arrange
        var assetId = Guid.NewGuid();
        var request = new GetReviewsRequest { Page = 1, PageSize = 10, SortDirection = SortDirection.DESC };
        var query = new GetReviewsQuery(assetId, request);
        var key = CacheKeys.ReviewsList(assetId, request);

        var items = new List<Review>
        {
            new()
            {
                Id = Guid.NewGuid(), AssetId = assetId, UserId = Guid.NewGuid(),
                Rating = 4, Comment = "Nice", CreatedAt = DateTimeOffset.UtcNow,
                User = new User { Id = Guid.NewGuid(), Username = "alice", Email = "a@a.com", PasswordHash = "h", Role = AppRoles.USER }
            }
        };
        var pagedResult = new PagedResult<Review>(items, 1, 1, 10);

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _reviewStoreMock.GetPaged(assetId, request, Arg.Any<CancellationToken>()).Returns(pagedResult);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Username.Should().Be("alice");

        await _cacheMock.Received(1).SetString(
            key, 
            Arg.Is<string>(s => s.Contains("alice")), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<CancellationToken>());
    }
}
