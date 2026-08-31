using Ardalis.Result;
using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Tags.GetTagById;
using AssetBlock.Domain.Core.Dto.Tags;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_ShouldRegisterSender()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISender sender = provider.GetRequiredService<ISender>();
        sender.Should().NotBeNull();
        sender.Should().BeOfType<Sender>();
    }

    [Fact]
    public void AddApplication_ShouldRegisterInternalHandlers()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IRequestHandler<GetTagByIdQuery, Result<TagDto>>)
            && descriptor.ImplementationType == typeof(GetTagByIdQueryHandler));
    }

    [Fact]
    public void AddApplication_ShouldRegisterPipelineBehaviorsInLoggingThenValidationOrder()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        var behaviors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        behaviors.Should().Equal(typeof(LoggingBehavior<,>), typeof(ValidationBehavior<,>));
    }
}
