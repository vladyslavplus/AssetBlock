using AssetBlock.Application.UseCases.Tags.GetTags;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class GetTagsQueryHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly GetTagsQueryHandler _handler;

    public GetTagsQueryHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new GetTagsQueryHandler(
            _tagStoreMock,
            _cacheMock,
            NullLogger<GetTagsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResult()
    {
        // Arrange
        var request = new GetTagsRequest { Search = "low", Page = 1, PageSize = 10 };
        var query = new GetTagsQuery(request);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var cachedJson = $$"""{"items":[{"id":"{{id1}}","name":"low-poly"},{"id":"{{id2}}","name":"low-res"}],"totalCount":2,"page":1,"pageSize":10,"totalPages":1}""";
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(cachedJson);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Name.Should().Be("low-poly");

        await _tagStoreMock.DidNotReceiveWithAnyArgs().SearchTags(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCache()
    {
        // Arrange
        var request = new GetTagsRequest { Search = "low", Page = 1, PageSize = 10 };
        var query = new GetTagsQuery(request);

        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var storedTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "low-poly" }
        };

        var pagedResult = new PagedResult<Tag>(storedTags, 1, 1, 10);
        _tagStoreMock.SearchTags(Arg.Any<GetTagsRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("low-poly");
        result.Value.TotalCount.Should().Be(1);

        await _cacheMock.Received(1).SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
