using System.IO.Pipes;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

[Collection(DriverLifecycleTestGroup.Name)]
public sealed class NamedPipeDriverTransportTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("path/to/pipe")]
    [InlineData("path\\to\\pipe")]
    public void FactoryRejectsNonLocalPipeNames(string pipeName)
    {
        Assert.ThrowsAny<ArgumentException>(() => new NamedPipeDriverTransportFactory(pipeName));
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task UnsupportedPlatformFailsClosedWithDeterministicAuthorizationDiagnostic()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var transport = new NamedPipeDriverTransport("ltb-test");

        var exception = await Assert.ThrowsAsync<DriverPeerAuthorizationException>(
            () => transport.ConnectAsync(CancellationToken.None).AsTask());

        Assert.Equal(
            "Named-pipe server authorization requires Windows session APIs.",
            exception.Message);
        Assert.False(transport.IsConnected);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task WriteBeforeConnectionClassifiesDisconnectAsRecoverableTransportFailure()
    {
        await using var transport = new NamedPipeDriverTransport("ltb-test");

        await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
            () => transport.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None).AsTask());
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task FakeTransportRunsCompleteFeedOnNonWindowsHost()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using IDriverFeed feed = new DriverFeed(
            new QueueDriverTransportFactory(transport),
            clock,
            sessionIdFactory: new QueueSessionIdFactory(DriverTestData.SessionA));

        await feed.StartAsync();
        await feed.PublishAsync(DriverTestData.State());

        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
        Assert.Equal(2, transport.Packets.Count);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task SameSessionAuthorizationCompletesBeforeConnectionCanPublish()
    {
        string pipeName = $"ltb-test-{Guid.NewGuid():N}";
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession();
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var transport = new NamedPipeDriverTransport(
            pipeName,
            new NamedPipePeerSessionVerifier(nativeApi));

        using var timeout = DriverTestTimeouts.CreatePhaseCancellation();
        Task serverConnection = server.WaitForConnectionAsync(timeout.Token);
        await DriverTestTimeouts.AwaitPhaseAsync(
            transport.ConnectAsync(timeout.Token).AsTask(),
            "client connect and same-session authorization",
            timeout.Token);
        await DriverTestTimeouts.AwaitPhaseAsync(
            serverConnection,
            "server accepts authorized client",
            timeout.Token);
        await DriverTestTimeouts.AwaitPhaseAsync(
            transport.WriteAsync(new byte[] { 1, 2, 3 }, timeout.Token).AsTask(),
            "authorized client writes packet",
            timeout.Token);

        var buffer = new byte[3];
        Task<int> read = server.ReadAsync(buffer, timeout.Token).AsTask();
        await DriverTestTimeouts.AwaitPhaseAsync(
            read,
            "server reads authorized packet",
            timeout.Token);
        int bytesRead = await read;
        Assert.Equal(3, bytesRead);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        Assert.True(transport.IsConnected);
        Assert.Equal(1, nativeApi.ServerProcessLookupCount);
        Assert.Equal(
            new[] { nativeApi.ServerProcessId, checked((uint)Environment.ProcessId) },
            nativeApi.SessionLookupProcessIds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task ConnectionIsNotPublishedAndCannotWriteWhileAuthorizationIsPending()
    {
        string pipeName = $"ltb-test-{Guid.NewGuid():N}";
        using var verifier = new BlockingNamedPipePeerSessionVerifier();
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var transport = new NamedPipeDriverTransport(pipeName, verifier);

        using var timeout = DriverTestTimeouts.CreatePhaseCancellation();
        Task serverConnection = server.WaitForConnectionAsync(timeout.Token);
        Task connect = transport.ConnectAsync(timeout.Token).AsTask();
        try
        {
            await DriverTestTimeouts.AwaitPhaseAsync(
                verifier.Entered.Task,
                "authorization verifier entered",
                timeout.Token);
            await DriverTestTimeouts.AwaitPhaseAsync(
                serverConnection,
                "server accepts client pending authorization",
                timeout.Token);
            Assert.False(transport.IsConnected);
            await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
                () => transport.WriteAsync(new byte[] { 1 }, CancellationToken.None).AsTask());
        }
        finally
        {
            verifier.Release();
        }

        await DriverTestTimeouts.AwaitPhaseAsync(
            connect,
            "client authorization completes after release",
            timeout.Token);
        Assert.True(transport.IsConnected);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task RejectedAuthorizationDisconnectsPipeAndPreventsWrites()
    {
        string pipeName = $"ltb-test-{Guid.NewGuid():N}";
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession() with
        {
            ClientSessionId = 12,
            ServerSessionId = 11,
        };
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var transport = new NamedPipeDriverTransport(
            pipeName,
            new NamedPipePeerSessionVerifier(nativeApi));

        using var timeout = DriverTestTimeouts.CreatePhaseCancellation();
        Task serverConnection = server.WaitForConnectionAsync(timeout.Token);
        var exception = await Assert.ThrowsAsync<DriverPeerAuthorizationException>(
            () => transport.ConnectAsync(timeout.Token).AsTask());
        await DriverTestTimeouts.AwaitPhaseAsync(
            serverConnection,
            "server accepts rejected client before disconnect",
            timeout.Token);

        Assert.Equal(
            "Named-pipe server authorization failed: server session 11 does not match client session 12.",
            exception.Message);
        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
            () => transport.WriteAsync(new byte[] { 1 }, CancellationToken.None).AsTask());
        var buffer = new byte[1];
        Task<int> read = server.ReadAsync(buffer, timeout.Token).AsTask();
        await DriverTestTimeouts.AwaitPhaseAsync(
            read,
            "server observes rejected client disconnect",
            timeout.Token);
        int bytesRead = await read;
        Assert.Equal(0, bytesRead);
    }
}
