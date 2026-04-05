using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ApiControllerBaseMappingTests : ControllerTestBase
{
    private sealed class MappingController(ISender sender) : ApiControllerBase(sender)
    {
        public IActionResult Map<T>(Result<T> r) => MapResultToActionResult(r);
        public IActionResult Map(Result r) => MapResultToActionResult(r);
    }

    private static readonly TokensResponse _sampleTokens = new("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void Map_Generic_WhenSuccess_ShouldReturnOkWithValue()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result.Success(_sampleTokens));
        action.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(_sampleTokens);
    }

    [Fact]
    public void Map_Generic_WhenInvalid_ShouldReturnBadRequest()
    {
        var c = new MappingController(Sender);
        var action = c.Map(ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS));
        action.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Map_Generic_WhenNotFound_ShouldReturnNotFound()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result<TokensResponse>.NotFound(ErrorCodes.ERR_USER_NOT_FOUND));
        action.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Map_Generic_WhenConflict_ShouldReturn409()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result<TokensResponse>.Conflict(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS));
        var status = action.Should().BeOfType<ObjectResult>().Which;
        status.StatusCode.Should().Be(409);
    }

    [Fact]
    public void Map_Generic_WhenForbidden_ShouldReturn403()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result<TokensResponse>.Forbidden(ErrorCodes.ERR_FORBIDDEN));
        var status = action.Should().BeOfType<ObjectResult>().Which;
        status.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Map_Generic_WhenUnauthorized_ShouldReturn401()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result<TokensResponse>.Unauthorized(ErrorCodes.ERR_AUTH_TOKEN_INVALID));
        action.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public void Map_NonGeneric_WhenSuccess_ShouldReturnOk()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result.Success());
        action.Should().BeOfType<OkResult>();
    }

    [Fact]
    public void Map_NonGeneric_WhenInvalid_ShouldReturnBadRequest()
    {
        var c = new MappingController(Sender);
        var action = c.Map(ResultError.Error(ErrorCodes.ERR_BAD_REQUEST));
        action.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Map_NonGeneric_WhenNotFound_ShouldReturnNotFound()
    {
        var c = new MappingController(Sender);
        var action = c.Map(Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND));
        action.Should().BeOfType<NotFoundObjectResult>();
    }
}
