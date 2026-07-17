using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class OpenVrRuntimeEventSemanticsTests
{
    [Theory]
    [InlineData(700u, "RuntimeStoppedAndAcknowledgeQuit")]
    [InlineData(701u, "Ignore")]
    [InlineData(704u, "RuntimeStopped")]
    [InlineData(0u, "Ignore")]
    public void ClassifiesRuntimeTerminalEventsWithoutTreatingProcessQuitAsSteamVrStop(
        uint eventType,
        string expected)
    {
        Assert.Equal(expected, OpenVrRuntimeEventSemantics.Classify(eventType).ToString());
    }
}
