using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.ProblemDetails;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.ProblemDetails;

/// <summary>
/// Isolated rate-limiter pipeline (IntegrationTesting uses no-op policies).
/// </summary>
public sealed class RateLimitProblemDetailsPipelineTests
{
    [Fact]
    public async Task OnRejected_ShouldReturnRateLimitedProblemDetails()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opts.OnRejected = async (context, _) =>
            {
                var problem = AssetBlockProblemDetails.Create(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    ErrorCodes.ERR_RATE_LIMITED);
                await AssetBlockProblemDetails.Write(context.HttpContext, problem);
            };
            opts.AddFixedWindowLimiter("one", options =>
            {
                options.PermitLimit = 1;
                options.Window = TimeSpan.FromMinutes(1);
                options.QueueLimit = 0;
                options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });

        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/probe", () => Results.Ok()).RequireRateLimiting("one");
        await app.StartAsync();

        var client = app.GetTestClient();
        var first = await client.GetAsync(new Uri("/probe", UriKind.Relative));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_RATE_LIMITED);
        doc.RootElement.GetProperty("type").GetString().Should().Be($"urn:assetblock:error:{ErrorCodes.ERR_RATE_LIMITED}");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeTrue();
    }
}
