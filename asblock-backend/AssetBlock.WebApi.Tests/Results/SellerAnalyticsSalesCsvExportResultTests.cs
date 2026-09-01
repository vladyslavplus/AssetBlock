using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Results;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Results;

public sealed class SellerAnalyticsSalesCsvExportResultTests
{
    [Fact]
    public async Task ExecuteResultAsync_WhenStreamingFailureOccurs_ShouldAbortWithoutAppendingProblemDetails()
    {
        ISellerAnalyticsSalesExportSession session = Substitute.For<ISellerAnalyticsSalesExportSession>();
        ISender sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<PrepareSellerAnalyticsSalesExportQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PreparedSellerAnalyticsSalesExport(session)));
        sender.Send(Arg.Any<ExportSellerAnalyticsSalesCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<int>.Error(ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE));

        var responseFeature = new StartedResponseFeature();
        var lifetimeFeature = new TrackingRequestLifetimeFeature();
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpResponseFeature>(responseFeature);
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetimeFeature);
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        var result = new SellerAnalyticsSalesCsvExportResult(
            Guid.NewGuid(),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            AnalyticsProductTypeFilter.ALL,
            sender);

        await result.ExecuteResultAsync(actionContext);

        lifetimeFeature.Aborted.Should().BeTrue();
        responseFeature.StatusCode.Should().Be(StatusCodes.Status200OK);
        responseFeature.Headers.ContentType.ToString().Should().Be("text/csv; charset=utf-8");
        responseFeature.Body.Length.Should().Be(0);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class TrackingRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
}
