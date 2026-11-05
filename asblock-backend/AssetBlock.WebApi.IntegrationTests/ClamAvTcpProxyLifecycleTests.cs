using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests;

public sealed class ClamAvTcpProxyLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_WhenRelayIsInFlight_ShouldJoinRelayAndCloseBothSockets()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetEndpoint = (IPEndPoint)target.LocalEndpoint;
        var relayPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new ClamAvTcpProxy("127.0.0.1", targetEndpoint.Port, async () =>
        {
            relayPaused.TrySetResult();
            await releaseRelay.Task;
        });
        proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        using TcpClient upstream = await target.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForInFlightRelay(proxy, TimeSpan.FromSeconds(5));

        Task firstDisposal = proxy.DisposeAsync().AsTask();
        Task repeatedDisposal = proxy.DisposeAsync().AsTask();
        firstDisposal.Should().BeSameAs(repeatedDisposal);
        try
        {
            await relayPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await proxy.AcceptLoopCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            firstDisposal.IsCompleted.Should().BeFalse();
            proxy.AcceptLoopCompleted.Should().BeTrue();
            proxy.TrackedRelayCount.Should().Be(1);
            proxy.IncompleteRelayCount.Should().Be(1);
            client.Client.Poll(0, SelectMode.SelectRead).Should().BeFalse();
            upstream.Client.Poll(0, SelectMode.SelectRead).Should().BeFalse();
        }
        finally
        {
            releaseRelay.TrySetResult();
            await firstDisposal.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForRelaysToFinish(proxy, TimeSpan.FromSeconds(5));
        }

        await repeatedDisposal;
        proxy.AcceptLoopCompleted.Should().BeTrue();
        proxy.TrackedRelayCount.Should().Be(1);
        proxy.IncompleteRelayCount.Should().Be(0);
        await AssertPeerClosed(client.GetStream());
        await AssertPeerClosed(upstream.GetStream());
        proxy.Dispose();
    }

    private static async Task WaitForInFlightRelay(ClamAvTcpProxy proxy, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (proxy.TrackedRelayCount == 1 && proxy.IncompleteRelayCount == 1)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The proxy did not retain the accepted in-flight relay.");
    }

    private static async Task WaitForRelaysToFinish(ClamAvTcpProxy proxy, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (proxy.IncompleteRelayCount == 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("A relay task remained incomplete after the test released its completion gate.");
    }

    private static async Task AssertPeerClosed(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytesRead = await stream.ReadAsync(new byte[1], timeout.Token);
        bytesRead.Should().Be(0);
    }
}
