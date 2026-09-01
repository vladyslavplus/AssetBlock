using System.Net;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.WebApi.IntegrationTests.ProblemDetails;

public sealed class AuthRateLimitMissingIpPipelineTests
{
    [Theory]
    [InlineData(RateLimitingConstants.Policies.AUTH_REGISTER)]
    [InlineData(RateLimitingConstants.Policies.AUTH_LOGIN)]
    [InlineData(RateLimitingConstants.Policies.AUTH_REFRESH)]
    public async Task AuthPolicy_WhenRemoteIpMissing_ShouldFailClosedWith429(string policy)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        builder.Services.AddApiRateLimiting();

        await using WebApplication app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/probe", () => Microsoft.AspNetCore.Http.Results.Ok())
            .RequireRateLimiting(policy);
        await app.StartAsync();

        HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("/probe", UriKind.Relative),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_RATE_LIMITED);
    }
}
