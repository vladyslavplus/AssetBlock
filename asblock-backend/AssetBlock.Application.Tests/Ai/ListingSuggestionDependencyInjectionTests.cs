using AssetBlock.Application.Ai;
using AssetBlock.Domain.Abstractions.Services;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Tests.Ai;

public sealed class ListingSuggestionDependencyInjectionTests
{
    [Fact]
    public void AddApplication_ShouldRegisterListingSuggestionOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IListingSuggestionOrchestrator)
            && descriptor.ImplementationType == typeof(ListingSuggestionOrchestrator));
    }
}
