using Ltb.App;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverMonotonicDeadlineSchedulerTests
{
    [Fact]
    public async Task AbsoluteDeadlinesDoNotAddWorkTimeToPollInterval()
    {
        ulong now = 0;
        var delays = new List<TimeSpan>();
        var scheduler = new InternalDriverMonotonicDeadlineScheduler(
            TimeSpan.FromMilliseconds(10),
            now);

        now += Milliseconds(3);
        await scheduler.WaitForNextAsync(
            () => now,
            DelayAndAdvanceAsync,
            CancellationToken.None);
        Assert.Equal(Milliseconds(10), now);

        now += Milliseconds(3);
        await scheduler.WaitForNextAsync(
            () => now,
            DelayAndAdvanceAsync,
            CancellationToken.None);

        Assert.Equal(Milliseconds(20), now);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(7)],
            delays);
        return;

        ValueTask DelayAndAdvanceAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            now += checked((ulong)delay.Ticks * 100UL);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void OverrunSkipsMissedSlotsWithoutCatchUpBurst()
    {
        var scheduler = new InternalDriverMonotonicDeadlineScheduler(
            TimeSpan.FromMilliseconds(10),
            startedNanoseconds: 0);

        var plan = scheduler.PlanNext(Milliseconds(25));
        var next = scheduler.PlanNext(Milliseconds(33));

        Assert.Equal(Milliseconds(30), plan.DeadlineNanoseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(5), plan.Delay);
        Assert.Equal(2UL, plan.SkippedDeadlineCount);
        Assert.Equal(Milliseconds(40), next.DeadlineNanoseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(7), next.Delay);
        Assert.Equal(0UL, next.SkippedDeadlineCount);
    }

    [Fact]
    public void ExactDeadlineDoesNotRequestAZeroDelay()
    {
        var scheduler = new InternalDriverMonotonicDeadlineScheduler(
            TimeSpan.FromMilliseconds(10),
            startedNanoseconds: 0);

        var plan = scheduler.PlanNext(Milliseconds(10));

        Assert.Equal(TimeSpan.Zero, plan.Delay);
        Assert.Equal(0UL, plan.SkippedDeadlineCount);
        Assert.Equal(Milliseconds(10), plan.DeadlineNanoseconds);
    }

    [Fact]
    public void OverrunLandingOnLaterDeadlineUsesThatSlotWithoutExtraSkip()
    {
        var scheduler = new InternalDriverMonotonicDeadlineScheduler(
            TimeSpan.FromMilliseconds(10),
            startedNanoseconds: 0);

        var plan = scheduler.PlanNext(Milliseconds(20));

        Assert.Equal(Milliseconds(20), plan.DeadlineNanoseconds);
        Assert.Equal(TimeSpan.Zero, plan.Delay);
        Assert.Equal(1UL, plan.SkippedDeadlineCount);
    }

    private static ulong Milliseconds(ulong value) => value * 1_000_000UL;
}
