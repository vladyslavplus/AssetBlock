using AssetBlock.Domain.Core.Analytics;
using AssetBlock.Domain.Core.Constants;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.UseCases.Analytics;

public class AnalyticsReferrerHostTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("Example.COM", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    [InlineData("https://example.com", "example.com")]
    [InlineData("http://example.com/path/to/page", "example.com")]
    [InlineData("https://example.com:8443/path?q=1#frag", "example.com")]
    [InlineData("https://user:pass@sub.example.com/x", "sub.example.com")]
    [InlineData("https://example.com?q=1", "example.com")]
    [InlineData("news.sub-domain.example.co.uk", "news.sub-domain.example.co.uk")]
    public void Normalize_WhenValueIsAHostOrUrl_ShouldReturnBareLowercaseHost(string raw, string expected)
    {
        AnalyticsReferrerHost.Normalize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("not a host")]
    [InlineData("under_score.com")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("-example.com")]
    [InlineData("example.com-")]
    [InlineData("exa..mple.com")]
    [InlineData("https://[::1]/x")]
    public void Normalize_WhenValueIsNotAValidHost_ShouldReturnNull(string? raw)
    {
        AnalyticsReferrerHost.Normalize(raw).Should().BeNull();
    }

    [Fact]
    public void Normalize_WhenLabelExceedsDnsLimit_ShouldReturnNull()
    {
        var host = new string('a', 64) + ".com";

        AnalyticsReferrerHost.Normalize(host).Should().BeNull();
    }

    [Fact]
    public void Normalize_WhenHostExceedsColumnLimit_ShouldReturnNull()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('a', 63), 5));
        host.Length.Should().BeGreaterThan(AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH);

        AnalyticsReferrerHost.Normalize(host).Should().BeNull();
    }
}
