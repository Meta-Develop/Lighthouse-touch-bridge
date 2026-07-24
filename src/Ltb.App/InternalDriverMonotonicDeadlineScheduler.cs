namespace Ltb.App;

/// <summary>
/// Advances an absolute monotonic deadline and skips missed slots rather than
/// accumulating observation and publication work into the poll period.
/// </summary>
internal sealed class InternalDriverMonotonicDeadlineScheduler
{
    private readonly ulong _intervalNanoseconds;
    private ulong _nextDeadlineNanoseconds;

    public InternalDriverMonotonicDeadlineScheduler(
        TimeSpan interval,
        ulong startedNanoseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _intervalNanoseconds = checked((ulong)interval.Ticks * 100UL);
        _nextDeadlineNanoseconds = AddSaturating(
            startedNanoseconds,
            _intervalNanoseconds);
    }

    public InternalDriverDeadlinePlan PlanNext(ulong nowNanoseconds)
    {
        var deadline = _nextDeadlineNanoseconds;
        ulong skipped = 0;
        if (nowNanoseconds > deadline && deadline != ulong.MaxValue)
        {
            var lateBy = nowNanoseconds - deadline;
            skipped = (lateBy / _intervalNanoseconds) +
                (lateBy % _intervalNanoseconds == 0 ? 0UL : 1UL);
            deadline = AddSaturating(
                deadline,
                MultiplySaturating(skipped, _intervalNanoseconds));
        }

        _nextDeadlineNanoseconds = AddSaturating(deadline, _intervalNanoseconds);
        var delayNanoseconds = deadline > nowNanoseconds
            ? deadline - nowNanoseconds
            : 0UL;
        return new InternalDriverDeadlinePlan(
            deadline,
            ToCeilingTimeSpan(delayNanoseconds),
            skipped);
    }

    public async ValueTask WaitForNextAsync(
        Func<ulong> getMonotonicNanoseconds,
        Func<TimeSpan, CancellationToken, ValueTask> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getMonotonicNanoseconds);
        ArgumentNullException.ThrowIfNull(delayAsync);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = PlanNext(getMonotonicNanoseconds());
        if (plan.Delay > TimeSpan.Zero)
        {
            await delayAsync(plan.Delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        // An overrun can legitimately land on an absolute deadline with no
        // sleep. Yield once so a perpetually late synchronous loop remains
        // cancellable and does not monopolize its caller.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TimeSpan ToCeilingTimeSpan(ulong nanoseconds)
    {
        var ticks = Math.Min(
            (ulong)long.MaxValue,
            (nanoseconds / 100UL) + (nanoseconds % 100UL == 0 ? 0UL : 1UL));
        return TimeSpan.FromTicks((long)ticks);
    }

    private static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static ulong MultiplySaturating(ulong left, ulong right) =>
        left != 0 && right > ulong.MaxValue / left
            ? ulong.MaxValue
            : left * right;
}

internal readonly record struct InternalDriverDeadlinePlan(
    ulong DeadlineNanoseconds,
    TimeSpan Delay,
    ulong SkippedDeadlineCount);
