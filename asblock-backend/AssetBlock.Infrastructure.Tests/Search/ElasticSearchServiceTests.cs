using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Search;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Search;

public sealed class ElasticSearchServiceTests
{
    [Fact]
    public async Task SearchAssets_throwsSearchUnavailable_whenClusterUnreachable()
    {
        var settings = new ElasticsearchClientSettings(new Uri("http://127.0.0.1:59231"))
            .DefaultIndex("assets")
            .RequestTimeout(TimeSpan.FromMilliseconds(400));
        var client = new ElasticsearchClient(settings);
        var sut = CreateSut(client);

        var act = async () => await sut.SearchAssets(new GetAssetsRequest { Page = 1, PageSize = 10 });
        await act.Should().ThrowAsync<SearchUnavailableException>();
    }

    [Fact]
    public async Task SearchAssets_buildsFiltersAndSort_thenThrowsWhenClusterUnreachable()
    {
        var settings = new ElasticsearchClientSettings(new Uri("http://127.0.0.1:59234"))
            .DefaultIndex("assets")
            .RequestTimeout(TimeSpan.FromMilliseconds(400));
        var client = new ElasticsearchClient(settings);
        var sut = CreateSut(client);

        var cat = Guid.NewGuid();
        var act = async () => await sut.SearchAssets(new GetAssetsRequest
        {
            Page = 2,
            PageSize = 5,
            Search = "  hello world  ",
            CategoryId = cat,
            Tags = [" Alpha ", "beta", "", "alpha"],
            MinPrice = 1,
            MaxPrice = 99,
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        });

        await act.Should().ThrowAsync<SearchUnavailableException>();

        await FluentActions.Invoking(() => sut.SearchAssets(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "Price",
            SortDirection = SortDirection.DESC
        })).Should().ThrowAsync<SearchUnavailableException>();

        await FluentActions.Invoking(() => sut.SearchAssets(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "Id",
            SortDirection = SortDirection.ASC
        })).Should().ThrowAsync<SearchUnavailableException>();

        await FluentActions.Invoking(() => sut.SearchAssets(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "Invalid",
            SortDirection = SortDirection.DESC
        })).Should().ThrowAsync<SearchUnavailableException>();
    }

    [Fact]
    public async Task IndexAsset_throws_whenClusterUnreachable()
    {
        var settings = new ElasticsearchClientSettings(new Uri("http://127.0.0.1:59232"))
            .DefaultIndex("assets")
            .RequestTimeout(TimeSpan.FromMilliseconds(400));
        var client = new ElasticsearchClient(settings);
        var sut = CreateSut(client);

        var doc = new AssetDocument
        {
            Id = Guid.NewGuid(),
            Title = "t",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var act = async () => await sut.IndexAsset(doc);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DeleteAsset_throwsSearchUnavailable_whenClusterUnreachable()
    {
        var settings = new ElasticsearchClientSettings(new Uri("http://127.0.0.1:59233"))
            .DefaultIndex("assets")
            .RequestTimeout(TimeSpan.FromMilliseconds(400));
        var client = new ElasticsearchClient(settings);
        var sut = CreateSut(client);

        var act = async () => await sut.DeleteAsset(Guid.NewGuid());
        await act.Should().ThrowAsync<SearchUnavailableException>();
    }

    private static ElasticSearchService CreateSut(ElasticsearchClient client)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new ElasticsearchOptions { DefaultIndex = "assets" });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        return new ElasticSearchService(client, opts, resilience, NullLogger<ElasticSearchService>.Instance);
    }
}
