using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Analytics;

namespace AssetBlock.Infrastructure.Tests.Persistence.Analytics;

public sealed class AnalyticsCollectionsSqlTests
{
    [Theory]
    [InlineData(AnalyticsCollectionSort.VIEWS, AnalyticsSortDirection.DESC, "views")]
    [InlineData(AnalyticsCollectionSort.VIEWS, AnalyticsSortDirection.ASC, "views")]
    [InlineData(AnalyticsCollectionSort.CLICKS, AnalyticsSortDirection.DESC, "item_clicks")]
    [InlineData(AnalyticsCollectionSort.CLICKS, AnalyticsSortDirection.ASC, "item_clicks")]
    public void BuildOrderBy_WhenEngagementSort_ShouldFallBackToRevenueWhenCoverageIncomplete(
        AnalyticsCollectionSort sort,
        AnalyticsSortDirection direction,
        string engagementColumn)
    {
        var orderBy = AnalyticsCollectionsSql.BuildOrderBy(sort, direction);

        orderBy.Should().Contain("available_from FROM coverage");
        orderBy.Should().Contain(engagementColumn);
        orderBy.Should().Contain("attributed_gross_revenue");
        orderBy.Should().Contain("DESC NULLS LAST");
    }

    [Fact]
    public void BuildOrderBy_WhenAttributedRevenue_ShouldNotDependOnCoverage()
    {
        var orderBy = AnalyticsCollectionsSql.BuildOrderBy(
            AnalyticsCollectionSort.ATTRIBUTED_REVENUE,
            AnalyticsSortDirection.DESC);

        orderBy.Should().Be(""" attributed_gross_revenue DESC, "CollectionId" ASC """);
        orderBy.Should().NotContain("coverage");
    }
}
