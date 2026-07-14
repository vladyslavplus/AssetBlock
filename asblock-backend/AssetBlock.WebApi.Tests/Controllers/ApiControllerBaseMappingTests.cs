using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Text;
using System.Text.Json;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ApiControllerBaseMappingTests : ControllerTestBase
{
    private sealed class MappingController(ISender sender) : ApiControllerBase(sender)
    {
        public IActionResult Map<T>(Result<T> r) => MapResultToActionResult(r);
        public IActionResult Map(Result r) => MapResultToActionResult(r);
    }

    private static readonly TokensResponse _sampleTokens = new("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static MappingController CreateController()
    {
        var c = new MappingController(Substitute.For<ISender>());
        var httpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            Response = { Body = new MemoryStream() }
        };
        c.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return c;
    }

    private static async Task AssertProblem(MappingController c, IActionResult action, int status, string code)
    {
        await action.ExecuteResultAsync(c.ControllerContext);
        c.HttpContext.Response.StatusCode.Should().Be(status);
        c.HttpContext.Response.ContentType.Should().StartWith("application/problem+json");

        c.HttpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(c.HttpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(status);
        root.GetProperty("type").GetString().Should().Be($"urn:assetblock:error:{code}");
        root.GetProperty("code").GetString().Should().Be(code);
        root.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    public void Map_Generic_WhenSuccess_ShouldReturnOkWithValue()
    {
        var c = CreateController();
        var action = c.Map(Result.Success(_sampleTokens));
        action.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(_sampleTokens);
    }

    [Fact]
    public async Task Map_Generic_WhenInvalid_ShouldReturnBadRequestProblem()
    {
        var c = CreateController();
        var action = c.Map(ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS));
        await AssertProblem(c, action, StatusCodes.Status400BadRequest, ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
    }

    [Fact]
    public async Task Map_Generic_WhenNotFound_ShouldReturnNotFoundProblem()
    {
        var c = CreateController();
        var action = c.Map(Result<TokensResponse>.NotFound(ErrorCodes.ERR_USER_NOT_FOUND));
        await AssertProblem(c, action, StatusCodes.Status404NotFound, ErrorCodes.ERR_USER_NOT_FOUND);
    }

    [Fact]
    public async Task Map_Generic_WhenConflict_ShouldReturn409Problem()
    {
        var c = CreateController();
        var action = c.Map(Result<TokensResponse>.Conflict(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS));
        await AssertProblem(c, action, StatusCodes.Status409Conflict, ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Map_Generic_WhenForbidden_ShouldReturn403Problem()
    {
        var c = CreateController();
        var action = c.Map(Result<TokensResponse>.Forbidden(ErrorCodes.ERR_FORBIDDEN));
        await AssertProblem(c, action, StatusCodes.Status403Forbidden, ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task Map_Generic_WhenUnauthorized_ShouldReturn401Problem()
    {
        var c = CreateController();
        var action = c.Map(Result<TokensResponse>.Unauthorized(ErrorCodes.ERR_AUTH_TOKEN_INVALID));
        await AssertProblem(c, action, StatusCodes.Status401Unauthorized, ErrorCodes.ERR_AUTH_TOKEN_INVALID);
    }

    [Fact]
    public async Task Map_Generic_WhenSearchUnavailable_ShouldReturn503Problem()
    {
        var c = CreateController();
        var action = c.Map(Result<TokensResponse>.Error(ErrorCodes.ERR_SEARCH_UNAVAILABLE));
        await AssertProblem(c, action, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ERR_SEARCH_UNAVAILABLE);
    }

    [Fact]
    public void Map_NonGeneric_WhenSuccess_ShouldReturnOk()
    {
        var c = CreateController();
        var action = c.Map(Result.Success());
        action.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Map_NonGeneric_WhenInvalid_ShouldReturnBadRequestProblem()
    {
        var c = CreateController();
        var action = c.Map(ResultError.Error(ErrorCodes.ERR_BAD_REQUEST));
        await AssertProblem(c, action, StatusCodes.Status400BadRequest, ErrorCodes.ERR_BAD_REQUEST);
    }

    [Fact]
    public async Task Map_NonGeneric_WhenErrorWithBadRequestCode_ShouldReturn500WithInternalSemantics()
    {
        var c = CreateController();
        // ResultStatus.Error must stay 500 even if someone passes ERR_BAD_REQUEST as the code payload.
        var action = c.Map(Result.Error(ErrorCodes.ERR_BAD_REQUEST));
        await AssertProblem(c, action, StatusCodes.Status500InternalServerError, ErrorCodes.ERR_BAD_REQUEST);
    }

    [Fact]
    public async Task Map_NonGeneric_WhenErrorInternal_ShouldReturn500Problem()
    {
        var c = CreateController();
        var action = c.Map(Result.Error(ErrorCodes.ERR_INTERNAL));
        await AssertProblem(c, action, StatusCodes.Status500InternalServerError, ErrorCodes.ERR_INTERNAL);
    }

    [Fact]
    public async Task Map_NonGeneric_WhenNotFound_ShouldReturnNotFoundProblem()
    {
        var c = CreateController();
        var action = c.Map(Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND));
        await AssertProblem(c, action, StatusCodes.Status404NotFound, ErrorCodes.ERR_TAG_NOT_FOUND);
    }
}
