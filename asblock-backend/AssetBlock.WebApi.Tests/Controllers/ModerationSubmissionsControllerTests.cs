using System.Security.Claims;
using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ModerationSubmissionsControllerTests
{
    [Fact]
    public async Task GetById_WhenModeratorReadsEvidence_ShouldDisableSharedAndPrivateCaching()
    {
        ISender sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<GetModerationSubmissionCaseQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<ModerationCaseSummary>.NotFound());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]))
        };
        var controller = new ModerationSubmissionsController(sender)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        await controller.GetById(Guid.NewGuid(), CancellationToken.None);
        context.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
    }
}
