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
    public async Task ConstructionHasNoWindowsOnlyInitializationOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var transport = new NamedPipeDriverTransport("ltb-test");

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => transport.ConnectAsync(CancellationToken.None).AsTask());
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
}
