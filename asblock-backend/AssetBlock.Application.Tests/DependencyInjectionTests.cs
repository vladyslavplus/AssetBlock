using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_ShouldRegisterMediator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();

        using var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        sender.Should().NotBeNull();
    }
}
