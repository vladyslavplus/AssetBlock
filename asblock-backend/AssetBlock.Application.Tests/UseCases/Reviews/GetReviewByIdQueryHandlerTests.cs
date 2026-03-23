using System.Text.Json;
using AssetBlock.Application.UseCases.Reviews.GetReviewById;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Dto.Reviews;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class GetReviewByIdQueryHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly GetReviewByIdQueryHandler _handler;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GetReviewByIdQueryHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _cacheMock = Substitute.For<ICacheService>();

        _handler = new GetReviewByIdQueryHandler(
            _reviewStoreMock,
            _cacheMock,
            NullLogger<GetReviewByIdQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCached_ShouldReturnFromCache()
    {
        // Arrange
        var query = new GetReviewByIdQuery(Guid.NewGuid());
        var key = CacheKeys.ReviewItem(query.Id);
        
        var cachedItem = new ReviewDetailItem(query.Id, Guid.NewGuid(), Guid.NewGuid(), "testuser", 5, "Great", DateTimeOffset.UtcNow);
        var cachedJson = JsonSerializer.Serialize(cachedItem, _jsonOptions);

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns(cachedJson);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(query.Id);
        result.Value.Username.Should().Be("testuser");
        
        await _reviewStoreMock.DidNotReceive().GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotCachedAndNotFound_ShouldReturnError()
    {
        // Arrange
        var query = new GetReviewByIdQuery(Guid.NewGuid());
        var key = CacheKeys.ReviewItem(query.Id);

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _reviewStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns((Review?)null);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenNotCachedAndFound_ShouldReturnAndCache()
    {
        // Arrange
        var query = new GetReviewByIdQuery(Guid.NewGuid());
        var key = CacheKeys.ReviewItem(query.Id);

        var review = new Review
        {
            Id = query.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            Rating = 5, Comment = "Awesome", CreatedAt = DateTimeOffset.UtcNow,
            User = new User { Id = Guid.NewGuid(), Username = "user123", Email = "a@a.com", PasswordHash = "h", Role = AppRoles.USER }
        };

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns((string?)null);
        _reviewStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns(review);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(query.Id);
        result.Value.Username.Should().Be("user123");

        await _cacheMock.Received(1).SetString(
            key, 
            Arg.Is<string>(s => s.Contains("user123")), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheInvalid_ShouldRemoveCacheAndFetchFromStore()
    {
        // Arrange
        var query = new GetReviewByIdQuery(Guid.NewGuid());
        var key = CacheKeys.ReviewItem(query.Id);

        var review = new Review
        {
            Id = query.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            Rating = 5, Comment = "Awesome", CreatedAt = DateTimeOffset.UtcNow,
            User = new User { Id = Guid.NewGuid(), Username = "user123", Email = "a@a.com", PasswordHash = "h", Role = AppRoles.USER }
        };

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns("invalid_json");
        _reviewStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns(review);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _cacheMock.Received(1).RemoveByPrefix(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheContainsNullJson_ShouldFetchFromStore()
    {
        var query = new GetReviewByIdQuery(Guid.NewGuid());
        var key = CacheKeys.ReviewItem(query.Id);

        _cacheMock.GetString(key, Arg.Any<CancellationToken>()).Returns("null");
        var review = new Review
        {
            Id = query.Id,
            AssetId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Rating = 3,
            Comment = "Ok",
            CreatedAt = DateTimeOffset.UtcNow,
            User = new User { Id = Guid.NewGuid(), Username = "u", Email = "a@a.com", PasswordHash = "h", Role = AppRoles.USER }
        };
        _reviewStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns(review);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Username.Should().Be("u");
    }
}
