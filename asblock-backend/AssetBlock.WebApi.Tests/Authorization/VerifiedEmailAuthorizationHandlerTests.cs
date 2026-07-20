using System.Security.Claims;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.WebApi.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Authorization;

public sealed class VerifiedEmailAuthorizationHandlerTests
{
    private readonly IUserVerificationStore _store = Substitute.For<IUserVerificationStore>();
    private readonly VerifiedEmailAuthorizationHandler _handler;

    public VerifiedEmailAuthorizationHandlerTests()
    {
        _handler = new VerifiedEmailAuthorizationHandler(_store);
    }

    [Fact]
    public async Task Handle_WhenEmailVerified_ShouldSucceed()
    {
        var userId = Guid.NewGuid();
        _store.IsEmailVerified(userId, Arg.Any<CancellationToken>()).Returns(true);
        var context = CreateContext(userId.ToString());

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenEmailNotVerified_ShouldNotSucceed()
    {
        var userId = Guid.NewGuid();
        _store.IsEmailVerified(userId, Arg.Any<CancellationToken>()).Returns(false);
        var context = CreateContext(userId.ToString());

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenSubjectMissing_ShouldNotSucceed()
    {
        var identity = new ClaimsIdentity("Test");
        identity.AddClaim(new Claim(ClaimTypes.Name, "no-sub"));
        var context = CreateContext(identity);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().IsEmailVerified(Guid.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenSubjectInvalid_ShouldNotSucceed()
    {
        var context = CreateContext("not-a-guid");

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().IsEmailVerified(Guid.Empty, CancellationToken.None);
    }

    private static AuthorizationHandlerContext CreateContext(string subject)
    {
        var identity = new ClaimsIdentity("Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        return CreateContext(identity);
    }

    private static AuthorizationHandlerContext CreateContext(ClaimsIdentity identity)
    {
        var user = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = user };
        return new AuthorizationHandlerContext(
            [new VerifiedEmailRequirement()],
            user,
            httpContext);
    }
}
