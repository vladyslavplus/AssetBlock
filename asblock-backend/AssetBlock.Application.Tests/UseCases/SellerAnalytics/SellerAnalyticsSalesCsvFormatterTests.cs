using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Core.Dto.Analytics;
using FluentAssertions;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class SellerAnalyticsSalesCsvFormatterTests
{
    [Fact]
    public void Header_ShouldMatchContractColumnOrder()
    {
        SellerAnalyticsSalesCsvFormatter.HEADER.Should().Be(
            "purchased_at_utc,order_id,product_type,product_id,product_title,units,gross_revenue_usd");
    }

    [Fact]
    public void Utf8Bom_ShouldBePresent()
    {
        SellerAnalyticsSalesCsvFormatter.Utf8Bom.Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public void FormatRow_ShouldUseInvariantDecimalAndIsoUtcTimestamp()
    {
        var row = new AnalyticsSalesExportRow(
            new DateTimeOffset(2024, 3, 2, 14, 30, 0, TimeSpan.FromHours(2)),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "ASSET",
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "Sample Asset",
            2,
            19.5m);

        var formatted = SellerAnalyticsSalesCsvFormatter.FormatRow(row);

        formatted.Should().StartWith("2024-03-02T12:30:00.0000000Z,aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee,ASSET,");
        formatted.Should().Contain(",Sample Asset,2,19.5");
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+123", "'+123")]
    [InlineData("-100", "'-100")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("\t+123", "'\t+123")]
    public void SanitizeFormulaInjection_ShouldPrefixApostrophe(string input, string expected)
    {
        SellerAnalyticsSalesCsvFormatter.SanitizeFormulaInjection(input).Should().Be(expected);
    }

    [Fact]
    public void EscapeField_ShouldQuoteCommasAndDoubleQuotes()
    {
        SellerAnalyticsSalesCsvFormatter.EscapeField("plain").Should().Be("plain");
        SellerAnalyticsSalesCsvFormatter.EscapeField("a,b").Should().Be("\"a,b\"");
        SellerAnalyticsSalesCsvFormatter.EscapeField("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
    }

    [Fact]
    public void FormatRow_ShouldApplyFormulaInjectionBeforeEscaping()
    {
        var row = new AnalyticsSalesExportRow(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            "ASSET",
            Guid.NewGuid(),
            "=HYPERLINK(\"evil\")",
            1,
            1m);

        SellerAnalyticsSalesCsvFormatter.FormatRow(row).Should().Contain("'=HYPERLINK(");
    }

    [Fact]
    public void FormatRow_WhenEmptyTitle_ShouldEmitEmptyField()
    {
        var row = new AnalyticsSalesExportRow(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            "BUNDLE",
            Guid.NewGuid(),
            "",
            0,
            0m);

        var formatted = SellerAnalyticsSalesCsvFormatter.FormatRow(row);
        formatted.Split(',').Should().HaveCount(7);
    }
}
