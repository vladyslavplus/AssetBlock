using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Tags.GetTags;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class GetTagsQueryHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly ITypedCache _cacheMock;
    private readonly GetTagsQueryHandler _handler;

    public GetTagsQueryHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ITypedCache>();
        _handler = new GetTagsQueryHandler(
            _tagStoreMock,
            _cacheMock,
            NullLogger<GetTagsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResult()
    {
        var request = new GetTagsRequest { Search = "low", Page = 1, PageSize = 10 };
        var query = new GetTagsQuery(request);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var cachedResult = new PagedResult<TagDto>([new TagDto(id1, "low-poly"), new TagDto(id2, "low-res")], 2, 1, 10);
        _cacheMock.Get<PagedResult<TagDto>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedResult);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Name.Should().Be("low-poly");

        await _tagStoreMock.DidNotReceiveWithAnyArgs().SearchTags(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCache()
    {
        var request = new GetTagsRequest { Search = "low", Page = 1, PageSize = 10 };
        var query = new GetTagsQuery(request);

        _cacheMock.Get<PagedResult<TagDto>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PagedResult<TagDto>?)null);

        var storedTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "low-poly" }
        };

        var pagedResult = new PagedResult<Tag>(storedTags, 1, 1, 10);
        _tagStoreMock.SearchTags(Arg.Any<GetTagsRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("low-poly");
        result.Value.TotalCount.Should().Be(1);

        await _cacheMock.Received(1).Set(Arg.Any<string>(), Arg.Any<PagedResult<TagDto>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
