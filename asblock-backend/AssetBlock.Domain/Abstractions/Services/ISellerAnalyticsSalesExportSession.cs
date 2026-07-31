using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Prepared sales export read. Single SQL pass determines row cap before CSV streaming begins.
/// </summary>
public interface ISellerAnalyticsSalesExportSession : IAsyncDisposable
{
    /// <summary>
    /// True when matching rows exceed <see cref="Core.Constants.AnalyticsConstants.MAX_SALES_EXPORT_ROWS"/>.
    /// </summary>
    bool ExceedsMax { get; }

    /// <summary>
    /// Streams export rows in PurchasedAt DESC, OrderId DESC order. Empty when <see cref="ExceedsMax"/> is true.
    /// </summary>
    IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(CancellationToken cancellationToken = default);
}
