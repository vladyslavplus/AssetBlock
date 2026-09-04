using System.Globalization;
using System.Text;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

internal sealed class ExportSellerAnalyticsSalesCommandHandler(
    IAuditWriter auditWriter,
    ILogger<ExportSellerAnalyticsSalesCommandHandler> logger,
    TimeSpan? auditTimeout = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<ExportSellerAnalyticsSalesCommand, Result<int>>
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly TimeSpan _auditTimeout = auditTimeout ?? TimeSpan.FromSeconds(5);

    public async Task<Result<int>> Handle(
        ExportSellerAnalyticsSalesCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Session.ExceedsMax)
        {
            return ResultError.Error(ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE);
        }

        await request.OutputStream.WriteAsync(SellerAnalyticsSalesCsvFormatter.Utf8Bom, cancellationToken);
        await WriteCsvLineAsync(request.OutputStream, SellerAnalyticsSalesCsvFormatter.HEADER, cancellationToken);

        var rowCount = 0;
        try
        {
            await foreach (AnalyticsSalesExportRow row in request.Session.ReadRows(cancellationToken))
            {
                await WriteCsvLineAsync(
                    request.OutputStream,
                    SellerAnalyticsSalesCsvFormatter.FormatRow(row),
                    cancellationToken);
                rowCount++;
            }
        }
        finally
        {
            await request.Session.DisposeAsync();
        }

        await request.OutputStream.FlushAsync(cancellationToken);

        await WriteExportAuditBestEffort(request, rowCount);

        logger.LogInformation(
            "Seller analytics sales export completed for seller {SellerId}: {RowCount} rows from {From} to {To}",
            request.SellerId,
            rowCount,
            request.From,
            request.To);

        return Result.Success(rowCount);
    }

    private async Task WriteExportAuditBestEffort(ExportSellerAnalyticsSalesCommand request, int rowCount)
    {
        using var auditCts = new CancellationTokenSource(_auditTimeout);
        try
        {
            DateTimeOffset generatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
            await auditWriter.WriteBestEffort(
                new AuditEvent(
                    AuditActions.SELLER_ANALYTICS_EXPORTED,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.SELLER_ANALYTICS,
                    request.SellerId.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["sellerId"] = request.SellerId.ToString(),
                        ["from"] = request.From.ToString("O", CultureInfo.InvariantCulture),
                        ["to"] = request.To.ToString("O", CultureInfo.InvariantCulture),
                        ["rowCount"] = rowCount,
                        ["generatedAt"] = generatedAt.ToString("O", CultureInfo.InvariantCulture)
                    },
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: request.SellerId),
                auditCts.Token);
        }
        catch (OperationCanceledException) when (auditCts.IsCancellationRequested)
        {
            logger.LogWarning(
                "Seller analytics export audit timed out after CSV flush for seller {SellerId} ({RowCount} rows)",
                request.SellerId,
                rowCount);
        }
    }

    private static async Task WriteCsvLineAsync(Stream outputStream, string line, CancellationToken cancellationToken)
    {
        var bytes = _utf8NoBom.GetBytes(line + "\r\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
    }
}
