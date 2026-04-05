using System.Security.Claims;
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

        var httpContext = new DefaultHttpContext
        {
            User = principal
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    protected static void SetupAnonymous(ControllerBase controller)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
    }
}
