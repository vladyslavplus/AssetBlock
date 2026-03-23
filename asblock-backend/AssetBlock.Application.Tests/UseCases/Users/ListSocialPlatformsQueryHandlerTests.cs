using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class ListSocialPlatformsQueryHandlerTests
{
    private readonly ISocialPlatformStore _store = Substitute.For<ISocialPlatformStore>();
    private readonly ListSocialPlatformsQueryHandler _handler;

    public ListSocialPlatformsQueryHandlerTests()
    {
        _handler = new ListSocialPlatformsQueryHandler(_store);
    }

    [Fact]
    public async Task Handle_ShouldReturnOrderedDtos()
    {
        var p2 = new SocialPlatform { Id = Guid.NewGuid(), Name = "Beta", IconName = "b" };
        var p1 = new SocialPlatform { Id = Guid.NewGuid(), Name = "Alpha", IconName = "a" };
        _store.GetAll(Arg.Any<CancellationToken>()).Returns([p2, p1]);

        var result = await _handler.Handle(new ListSocialPlatformsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value![0].Name.Should().Be("Alpha");
        result.Value[1].Name.Should().Be("Beta");
        result.Value[0].IconName.Should().Be("a");
    }
}
