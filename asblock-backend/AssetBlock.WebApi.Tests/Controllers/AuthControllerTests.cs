using Ardalis.Result;
using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Application.UseCases.Auth.RefreshToken;
using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class AuthControllerTests : ControllerTestBase
{
    private static readonly TokensResponse _tokens = new("access", "refresh", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Login_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<LoginCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(_tokens)));

        var controller = new AuthController(Sender);
        var result = await controller.Login(new LoginRequest("a@b.c", "pwd"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(_tokens);
    }

    [Fact]
    public async Task Refresh_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<RefreshTokenCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(_tokens)));

        var controller = new AuthController(Sender);
        var result = await controller.Refresh(new RefreshTokenRequest("rt"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Register_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<RegisterCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(_tokens)));

        var controller = new AuthController(Sender);
        var result = await controller.Register(new RegisterRequest("user", "a@b.c", "pwd"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
