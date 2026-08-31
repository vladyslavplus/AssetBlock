using System.Runtime.CompilerServices;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Analytics;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class SellerAnalyticsSalesExportSession : ISellerAnalyticsSalesExportSession
{
    private readonly ApplicationDbContext? _db;
    private readonly bool _disposeContext;
    private readonly IAsyncEnumerator<AnalyticsSaleExportSqlRow>? _enumerator;
    private readonly AnalyticsSalesExportRow? _firstRow;
    private int _disposed;
    private bool _readStarted;

    private SellerAnalyticsSalesExportSession(
        ApplicationDbContext? db,
        bool disposeContext,
        bool exceedsMax,
        IAsyncEnumerator<AnalyticsSaleExportSqlRow>? enumerator,
        AnalyticsSalesExportRow? firstRow)
    {
        _db = db;
        _disposeContext = disposeContext;
        ExceedsMax = exceedsMax;
        _enumerator = enumerator;
        _firstRow = firstRow;
    }

    public bool ExceedsMax { get; }

    public async IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (ExceedsMax || _enumerator is null || _readStarted)
        {
            yield break;
        }

        _readStarted = true;

        if (_firstRow is not null)
        {
            yield return _firstRow;
        }

        while (await _enumerator.MoveNextAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return MapRow(_enumerator.Current);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_enumerator is not null)
            {
                await _enumerator.DisposeAsync();
            }
        }
        finally
        {
            if (_disposeContext && _db is not null)
            {
                await _db.DisposeAsync();
            }
        }
    }

    internal static async Task<ISellerAnalyticsSalesExportSession> OpenAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        bool disposeContext,
        CancellationToken cancellationToken)
    {
        var sql = SalesExportSql.BuildExportQuery(productType);
        var peekLimit = AnalyticsConstants.MAX_SALES_EXPORT_ROWS + 1;

        ApplicationDbContext? db = null;
        IAsyncEnumerator<AnalyticsSaleExportSqlRow>? enumerator = null;

        try
        {
            db = await dbFactory.CreateDbContextAsync(cancellationToken);
            IAsyncEnumerable<AnalyticsSaleExportSqlRow> query = db.Database
                .SqlQueryRaw<AnalyticsSaleExportSqlRow>(sql, sellerId, from, to, peekLimit)
                .AsAsyncEnumerable();
            enumerator = query.GetAsyncEnumerator(cancellationToken);

            if (!await enumerator.MoveNextAsync())
            {
                return new SellerAnalyticsSalesExportSession(db, disposeContext, false, enumerator, null);
            }

            AnalyticsSaleExportSqlRow first = enumerator.Current;
            if (first.PeekCount > AnalyticsConstants.MAX_SALES_EXPORT_ROWS)
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                finally
                {
                    enumerator = null;
                    if (disposeContext)
                    {
                        await db.DisposeAsync();
                        db = null;
                    }
                }

                return new SellerAnalyticsSalesExportSession(null, false, true, null, null);
            }

            return new SellerAnalyticsSalesExportSession(
                db,
                disposeContext,
                false,
                enumerator,
                MapRow(first));
        }
        catch
        {
            try
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }
            }
            finally
            {
                if (disposeContext && db is not null)
                {
                    await db.DisposeAsync();
                }
            }

            throw;
        }
    }

    private static AnalyticsSalesExportRow MapRow(AnalyticsSaleExportSqlRow row)
    {
        if (row.Units <= 0)
        {
            throw new InvalidOperationException(
                $"Order {row.OrderId} matched seller filter but has no seller line stats.");
        }

        return new AnalyticsSalesExportRow(
            row.PurchasedAt,
            row.OrderId,
            row.ProductKind == 1
                ? nameof(AnalyticsProductKind.BUNDLE)
                : nameof(AnalyticsProductKind.ASSET),
            row.ProductId,
            row.ProductTitle,
            row.Units,
            row.GrossRevenue);
    }
}
