using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.ProblemDetails;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace AssetBlock.WebApi.Results;

internal sealed class SellerAnalyticsSalesCsvExportResult(
    Guid sellerId,
    DateOnly from,
    DateOnly to,
    AnalyticsProductTypeFilter productType,
    ISender sender) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        var cancellationToken = httpContext.RequestAborted;

        var prepareResult = await sender.Send(
            new PrepareSellerAnalyticsSalesExportQuery(sellerId, from, to, productType),
            cancellationToken);

        if (!prepareResult.IsSuccess)
        {
            var gateActionResult = ResultProblemDetailsMapper.Map(httpContext, prepareResult);
            await gateActionResult.ExecuteResultAsync(context);
            return;
        }

        await using var session = prepareResult.Value.Session;

        httpContext.Response.ContentType = "text/csv; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = SellerAnalyticsExportFileNames.SalesCsv(from, to)
        }.ToString();

        var command = new ExportSellerAnalyticsSalesCommand(
            sellerId,
            from,
            to,
            productType,
            httpContext.Response.Body,
            session);

        var result = await sender.Send(command, cancellationToken);
        if (!result.IsSuccess)
        {
            var actionResult = ResultProblemDetailsMapper.Map(httpContext, result);
            await actionResult.ExecuteResultAsync(context);
        }
    }
}
