using System.IO.Pipes;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

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

    [Fact]
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

    [Fact]
    public async Task WriteBeforeConnectionClassifiesDisconnectAsRecoverableTransportFailure()
    {
        await using var transport = new NamedPipeDriverTransport("ltb-test");

        await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
            () => transport.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None).AsTask());
    }

    [Fact]
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

    [Fact]
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

        Task serverConnection = server.WaitForConnectionAsync();
        await transport.ConnectAsync(CancellationToken.None);
        await serverConnection;
        await transport.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        var buffer = new byte[3];
        int bytesRead = await server.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(3, bytesRead);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        Assert.True(transport.IsConnected);
        Assert.Equal(1, nativeApi.ServerProcessLookupCount);
        Assert.Equal(
            new[] { nativeApi.ServerProcessId, checked((uint)Environment.ProcessId) },
            nativeApi.SessionLookupProcessIds);
    }

    [Fact]
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

        Task serverConnection = server.WaitForConnectionAsync();
        Task connect = transport.ConnectAsync(CancellationToken.None).AsTask();
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverConnection;
        try
        {
            Assert.False(transport.IsConnected);
            await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
                () => transport.WriteAsync(new byte[] { 1 }, CancellationToken.None).AsTask());
        }
        finally
        {
            verifier.Release();
        }

        await connect;
        Assert.True(transport.IsConnected);
    }

    [Fact]
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

        Task serverConnection = server.WaitForConnectionAsync();
        var exception = await Assert.ThrowsAsync<DriverPeerAuthorizationException>(
            () => transport.ConnectAsync(CancellationToken.None).AsTask());
        await serverConnection;

        Assert.Equal(
            "Named-pipe server authorization failed: server session 11 does not match client session 12.",
            exception.Message);
        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<DriverTransportDisconnectedException>(
            () => transport.WriteAsync(new byte[] { 1 }, CancellationToken.None).AsTask());
        var buffer = new byte[1];
        int bytesRead = await server.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(0, bytesRead);
    }
}
