using System.Text.Json;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class AnalyticsEnumSerializationTests
{
    private static readonly JsonSerializerOptions _options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void AnalyticsGranularity_SerializesAsString()
    {
        var json = JsonSerializer.Serialize(AnalyticsGranularity.DAY, _options);
        json.Should().Be("\"DAY\"");
    }

    [Fact]
    public void AnalyticsProductSort_SerializesAsString()
    {
        var json = JsonSerializer.Serialize(AnalyticsProductSort.RECENT, _options);
        json.Should().Be("\"RECENT\"");
    }

    [Fact]
    public void AnalyticsProductTypeFilter_SerializesAsString()
    {
        var json = JsonSerializer.Serialize(AnalyticsProductTypeFilter.BUNDLE, _options);
        json.Should().Be("\"BUNDLE\"");
    }
}
