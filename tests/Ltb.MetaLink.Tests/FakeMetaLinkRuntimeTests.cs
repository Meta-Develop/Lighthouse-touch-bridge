using System.Numerics;
using Ltb.Core;

namespace Ltb.MetaLink.Tests;

public sealed class FakeMetaLinkRuntimeTests
{
    [Fact]
    public void ReplaysSamplesInOrderThenRepeatsLatest()
    {
        var missing = Failure(0, MetaLinkReadiness.NotInstalled);
        var live = Live(1);
        using var runtime = new FakeMetaLinkRuntime(missing);
        runtime.Enqueue(live);

        var first = runtime.Poll();
        var repeated = runtime.Poll();

        Assert.Same(live, first);
        Assert.Same(live, repeated);
        Assert.True(runtime.TryGetLatest(MetaLinkHand.Left, out var left));
        Assert.Same(live.Left.Controller, left);
        Assert.Equal(2, runtime.PollCount);
    }

    [Fact]
    public void ResetClearsQueuedSamplesAndDisposeIsIdempotent()
    {
        var initial = Failure(0, MetaLinkReadiness.HeadsetDisconnected);
        var runtime = new FakeMetaLinkRuntime(initial);
        runtime.Enqueue(Live(1));

        runtime.Reset();
        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var resetLeft));
        var current = runtime.Poll();
        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(MetaLinkReadiness.RuntimeStopped, current.Left.Readiness);
        Assert.Null(resetLeft);
        Assert.Equal(1, runtime.ResetCount);
        Assert.True(runtime.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => runtime.Poll());
    }

    [Fact]
    public void ReadinessContractContainsExactlySevenSpecifiedStates()
    {
        Assert.Equal(
            [
                nameof(MetaLinkReadiness.NotInstalled),
                nameof(MetaLinkReadiness.AbiUnavailable),
                nameof(MetaLinkReadiness.RuntimeStopped),
                nameof(MetaLinkReadiness.HeadsetDisconnected),
                nameof(MetaLinkReadiness.ControllersUnavailable),
                nameof(MetaLinkReadiness.Ready),
                nameof(MetaLinkReadiness.Faulted),
            ],
            Enum.GetNames<MetaLinkReadiness>());
    }

    [Theory]
    [InlineData(MetaLinkReadiness.NotInstalled)]
    [InlineData(MetaLinkReadiness.AbiUnavailable)]
    [InlineData(MetaLinkReadiness.RuntimeStopped)]
    [InlineData(MetaLinkReadiness.HeadsetDisconnected)]
    [InlineData(MetaLinkReadiness.ControllersUnavailable)]
    [InlineData(MetaLinkReadiness.Faulted)]
    public void NonReadyHandCannotCarryControllerState(MetaLinkReadiness readiness)
    {
        var controller = Controller(MetaLinkHand.Left, 1d);

        var error = Assert.Throws<ArgumentException>(() => new MetaLinkHandSnapshot(
            MetaLinkHand.Left,
            readiness,
            "synthetic failure",
            controller));

        Assert.Contains("non-ready", error.Message, StringComparison.Ordinal);
    }

    private static MetaLinkRuntimeSnapshot Failure(long sequence, MetaLinkReadiness readiness) => new(
        sequence,
        sequence,
        new MetaLinkHandSnapshot(MetaLinkHand.Left, readiness, "synthetic failure"),
        new MetaLinkHandSnapshot(MetaLinkHand.Right, readiness, "synthetic failure"));

    private static MetaLinkRuntimeSnapshot Live(long sequence) => new(
        sequence,
        sequence,
        new MetaLinkHandSnapshot(
            MetaLinkHand.Left,
            MetaLinkReadiness.Ready,
            "left live",
            Controller(MetaLinkHand.Left, sequence)),
        new MetaLinkHandSnapshot(
            MetaLinkHand.Right,
            MetaLinkReadiness.Ready,
            "right live",
            Controller(MetaLinkHand.Right, sequence)));

    private static MetaLinkControllerSnapshot Controller(MetaLinkHand hand, double time) => new(
        hand,
        new MetaLinkPoseSnapshot(
            RigidTransform.Identity,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            isOrientationTracked: true,
            isPositionTracked: true,
            hasValidOrientation: true,
            hasValidPosition: true,
            rawMetaTimeSeconds: time,
            appMonotonicTimeSeconds: time,
            appMonotonicTimeNanoseconds: checked((long)(time * 1_000_000_000d)),
            clockUncertaintySeconds: 0d),
        default,
        default,
        default,
        MetaLinkBatteryState.Unavailable);
}
