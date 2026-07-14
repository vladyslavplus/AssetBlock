using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Common;

public abstract class ControllerTestBase
{
    protected ISender Sender { get; } = Substitute.For<ISender>();

    protected static void SetupUser(Guid userId, ControllerBase controller)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };

        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContext(principal)
        };
    }

    protected static void SetupAnonymous(ControllerBase controller)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContext(new ClaimsPrincipal(new ClaimsIdentity()))
        };
    }

    protected static async Task AssertStatusCodeAsync(ControllerBase controller, IActionResult action, int expectedStatus)
    {
        await action.ExecuteResultAsync(controller.ControllerContext);
        controller.HttpContext.Response.StatusCode.Should().Be(expectedStatus);
    }

    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal user) =>
        new()
        {
            User = user,
            Response = { Body = new MemoryStream() }
        };
}
