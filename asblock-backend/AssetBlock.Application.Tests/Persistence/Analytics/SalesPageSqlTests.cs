using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Analytics;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Persistence.Analytics;

public sealed class SalesPageSqlTests
{
    [Theory]
    [InlineData(AnalyticsProductTypeFilter.ALL, false)]
    [InlineData(AnalyticsProductTypeFilter.ASSET, true)]
    [InlineData(AnalyticsProductTypeFilter.BUNDLE, true)]
    public void Build_ValidProductType_ContainsExpectedFilter(AnalyticsProductTypeFilter type, bool hasProductPredicate)
    {
        var sql = SalesPageSql.Build(type, hasCursor: false);

        sql.Should().Contain("page_orders");
        sql.Should().Contain("line_stats");
        if (type == AnalyticsProductTypeFilter.ASSET)
        {
            sql.Should().Contain("AssetId");
        }
        else if (type == AnalyticsProductTypeFilter.BUNDLE)
        {
            sql.Should().Contain("BundleId");
        }

        if (!hasProductPredicate)
        {
            sql.Should().NotContain("AssetId IS NOT NULL");
            sql.Should().NotContain("BundleId IS NOT NULL");
        }
    }

    [Fact]
    public void Build_InvalidProductType_Throws()
    {
        Func<string> act = () => SalesPageSql.Build((AnalyticsProductTypeFilter)999, hasCursor: false);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("productType");
    }
}
