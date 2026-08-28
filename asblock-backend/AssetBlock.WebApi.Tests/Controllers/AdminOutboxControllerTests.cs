using Ardalis.Result;
using AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;
using AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class AdminOutboxControllerTests : ControllerTestBase
{
    [Fact]
    public void Controller_ShouldHaveAdminAndVerifiedEmailAuthorizationAttributes()
    {
        var type = typeof(AdminOutboxController);
        var authAttributes = type.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().ToList();

        authAttributes.Should().Contain(a => a.Roles == AppRoles.ADMIN);
        authAttributes.Should().Contain(a => a.Policy == AuthorizationPolicies.VERIFIED_EMAIL);
    }

    [Fact]
    public void Replay_ShouldHaveAdminOutboxReplayRateLimitingAttribute()
    {
        var method = typeof(AdminOutboxController).GetMethod(nameof(AdminOutboxController.Replay));
        var rateLimitAttribute = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), true)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
            .SingleOrDefault();

        rateLimitAttribute.Should().NotBeNull();
        rateLimitAttribute.PolicyName.Should().Be(RateLimitingConstants.Policies.ADMIN_OUTBOX_REPLAY);
    }

    [Fact]
    public async Task GetDeadLetters_WhenNullRequest_ShouldUseDefaultAndReturnOk()
    {
        var expected = new DomainPaging.PagedResult<DeadLetterOutboxListItemDto>(
            [new(Guid.NewGuid(), "EmailDispatch", DateTimeOffset.UtcNow, 10, DateTimeOffset.UtcNow, "failed", 0, null)],
            1,
            1,
            20);

        Sender.Send(Arg.Any<GetDeadLettersQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(expected)));

        var controller = new AdminOutboxController(Sender) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.GetDeadLetters(null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Replay_WhenSuccess_ShouldReturnOk()
    {
        var id = Guid.NewGuid();
        var response = new ReplayDeadLetterResponseDto(id, DateTimeOffset.UtcNow, 1);

        Sender.Send(Arg.Any<ReplayDeadLetterCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(response)));

        var controller = new AdminOutboxController(Sender) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.Replay(id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().BeEquivalentTo(response);
    }

    [Fact]
    public async Task Replay_WhenNotFound_ShouldReturn404()
    {
        var id = Guid.NewGuid();

        Sender.Send(Arg.Any<ReplayDeadLetterCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReplayDeadLetterResponseDto>.NotFound(ErrorCodes.ERR_OUTBOX_MESSAGE_NOT_FOUND)));

        var controller = new AdminOutboxController(Sender) { ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } }
            }
        };

        var result = await controller.Replay(id, CancellationToken.None);

        await result.ExecuteResultAsync(controller.ControllerContext);
        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Replay_WhenNotDeadLettered_ShouldReturn409()
    {
        var id = Guid.NewGuid();

        Sender.Send(Arg.Any<ReplayDeadLetterCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReplayDeadLetterResponseDto>.Conflict(ErrorCodes.ERR_OUTBOX_NOT_DEAD_LETTERED)));

        var controller = new AdminOutboxController(Sender) { ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } }
            }
        };

        var result = await controller.Replay(id, CancellationToken.None);

        await result.ExecuteResultAsync(controller.ControllerContext);
        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }
}
