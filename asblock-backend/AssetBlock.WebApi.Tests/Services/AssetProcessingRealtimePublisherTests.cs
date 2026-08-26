using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Hubs;
using AssetBlock.WebApi.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Services;

public sealed class AssetProcessingRealtimePublisherTests
{
    [Fact]
    public async Task PublishJobUpdated_ShouldSendClientMessageToTargetUser()
    {
        var ownerUserId = Guid.NewGuid();
        var message = new AssetProcessingUpdateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingJobStatus.RUNNING,
            "RUNNING",
            DateTimeOffset.UtcNow);

        var clientProxy = Substitute.For<IClientProxy>();
        clientProxy.SendCoreAsync(
                NotificationsHub.ASSET_PROCESSING_UPDATED,
                Arg.Is<object?[]>(args => args.Length == 1 && Equals(args[0], message)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var clients = Substitute.For<IHubClients>();
        clients.User(ownerUserId.ToString()).Returns(clientProxy);

        var hubContext = Substitute.For<IHubContext<NotificationsHub>>();
        hubContext.Clients.Returns(clients);

        var publisher = new AssetProcessingRealtimePublisher(
            hubContext,
            NullLogger<AssetProcessingRealtimePublisher>.Instance);

        await publisher.PublishJobUpdated(ownerUserId, message, CancellationToken.None);

        await clientProxy.Received(1).SendCoreAsync(
            NotificationsHub.ASSET_PROCESSING_UPDATED,
            Arg.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], message)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishJobUpdated_WhenHubThrows_ShouldCatchAndNotThrow()
    {
        var ownerUserId = Guid.NewGuid();
        var message = new AssetProcessingUpdateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingJobStatus.RUNNING,
            "RUNNING",
            DateTimeOffset.UtcNow);

        var clientProxy = Substitute.For<IClientProxy>();
        clientProxy.SendCoreAsync(
                Arg.Any<string>(),
                Arg.Any<object?[]>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("SignalR network failure"));

        var clients = Substitute.For<IHubClients>();
        clients.User(ownerUserId.ToString()).Returns(clientProxy);

        var hubContext = Substitute.For<IHubContext<NotificationsHub>>();
        hubContext.Clients.Returns(clients);

        var publisher = new AssetProcessingRealtimePublisher(
            hubContext,
            NullLogger<AssetProcessingRealtimePublisher>.Instance);

        var act = () => publisher.PublishJobUpdated(ownerUserId, message, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AssetProcessingUpdateMessage_Serialization_ShouldProduceStringEnums()
    {
        var message = new AssetProcessingUpdateMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.MALWARE_SCAN,
            AssetProcessingJobStatus.SUCCEEDED,
            "CLEAN",
            DateTimeOffset.UtcNow);

        var json = System.Text.Json.JsonSerializer.Serialize(message);

        json.Should().Contain("\"Type\":\"MALWARE_SCAN\"");
        json.Should().Contain("\"Status\":\"SUCCEEDED\"");
    }
}
