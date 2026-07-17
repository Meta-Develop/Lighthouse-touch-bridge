using Ltb.App;
using Ltb.Core;

namespace Ltb.Integration.Tests;

public sealed class ReliableDailyUseStateMachineTests
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(17);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(83);

    [Fact]
    public async Task LaterRunUsesRequiredStartupSequenceAndCancellationSafelyStops()
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(cancellation);
        context.Runtime.CancelOnMonitorDelayNumber = 1;

        var result = await context.Coordinator.RunAsync(cancellation.Token);

        Assert.Equal(ReliableDailyUseStopReason.Cancellation, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.Equal(
            [
                RuntimeApplicationState.Stopped,
                RuntimeApplicationState.DependencyCheck,
                RuntimeApplicationState.WaitingForSteamVR,
                RuntimeApplicationState.WaitingForDevices,
                RuntimeApplicationState.Ready,
                RuntimeApplicationState.ApplyProfile,
                RuntimeApplicationState.Active,
                RuntimeApplicationState.SafeDisable,
                RuntimeApplicationState.Stopped,
            ],
            result.StateHistory);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.ShutdownRequested);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SafeDisableCompleted);
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task TrackerLossDisablesBeforeStableSerialReacquisitionAndRejectsHandleChurn()
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(cancellation, queueInitialDevices: false);
        context.Runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 3);
        context.Runtime.QueueDeviceSet(
            new CalibrationWizardDeviceSet(
                "TOUCH-LEFT",
                "TOUCH-RIGHT",
                ["TRACKER-LEFT", "TRACKER-WRONG"]),
            transientHandle: 57);
        context.Runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 103);
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.TrackerLost,
                "left tracker disconnected",
                hand: "left",
                deviceIdentity: "TRACKER-LEFT"));
        context.Runtime.CancelOnMonitorDelayNumber = 2;

        var result = await context.Coordinator.RunAsync(cancellation.Token);

        Assert.Equal(ReliableDailyUseStopReason.Cancellation, result.StopReason);
        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.WaitingForDevices,
            RuntimeApplicationState.Ready,
            RuntimeApplicationState.ApplyProfile,
            RuntimeApplicationState.Active);
        Assert.Equal([3, 3, 103, 103], context.Runtime.AppliedTransientHandles);
        Assert.DoesNotContain(57, context.Runtime.AppliedTransientHandles);
        Assert.Equal(0, context.Runtime.StaleApplyAttempts);
        Assert.Contains(ReconnectDelay, context.Runtime.Delays);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.TrackerLost &&
                     entry.Properties["deviceIdentity"] == "TRACKER-LEFT");
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.ReconnectWaiting);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.Reconnected);

        var firstDisable = context.Runtime.Journal.IndexOf("deactivate:right:3");
        var firstReacquiredCreate = context.Runtime.Journal.IndexOf("create:left:103");
        Assert.True(firstDisable >= 0 && firstDisable < firstReacquiredCreate);
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task TouchLossUsesSafeDisableBeforeReapply()
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(cancellation);
        context.Runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 4);
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.TouchInputLost,
                "right Touch input disappeared",
                hand: "right",
                deviceIdentity: "TOUCH-RIGHT"));
        context.Runtime.CancelOnMonitorDelayNumber = 2;

        var result = await context.Coordinator.RunAsync(cancellation.Token);

        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.WaitingForDevices);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.TouchInputLost &&
                     entry.Properties["hand"] == "right");
        Assert.Equal(2, result.StateHistory.Count(
            state => state == RuntimeApplicationState.Active));
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task VmtLossRechecksDependenciesBeforeReapply()
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(cancellation);
        context.Runtime.QueueDependencies(
            new CalibrationWizardDependencyStatus(true, true, "initial dependencies ready"),
            new CalibrationWizardDependencyStatus(true, false, "VMT heartbeat unavailable"),
            new CalibrationWizardDependencyStatus(true, true, "VMT heartbeat recovered"));
        context.Runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 203);
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.VmtUnavailable,
                "VMT heartbeat became stale"));
        context.Runtime.CancelOnMonitorDelayNumber = 2;

        var result = await context.Coordinator.RunAsync(cancellation.Token);

        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.DependencyCheck,
            RuntimeApplicationState.WaitingForSteamVR,
            RuntimeApplicationState.WaitingForDevices,
            RuntimeApplicationState.Ready,
            RuntimeApplicationState.ApplyProfile,
            RuntimeApplicationState.Active);
        Assert.Equal(3, context.Runtime.DependencyCheckCalls);
        Assert.Contains(ReconnectDelay, context.Runtime.Delays);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.VmtUnavailable);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.DependencyUnavailable);
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task SteamVrStoppingDuringVmtRecoveryIsTerminalAndNeverReapplies()
    {
        var context = CreateContext();
        context.Runtime.StopSteamVrOnWaitCall = 2;
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.VmtUnavailable,
                "VMT heartbeat became stale before SteamVR stopped"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SteamVrStopped, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.DependencyCheck,
            RuntimeApplicationState.WaitingForSteamVR,
            RuntimeApplicationState.Stopped);
        Assert.Equal(1, result.StateHistory.Count(
            state => state == RuntimeApplicationState.Active));
        Assert.Equal(2, context.Runtime.AppliedTransientHandles.Count);
        Assert.Equal(2, context.Runtime.WaitForSteamVrCalls);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SteamVrStopped &&
                     entry.State == RuntimeApplicationState.WaitingForSteamVR);
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task StartupReadinessLogsDistinctBoundedDiagnosticsBeforeAnyApply()
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(cancellation, queueInitialDevices: false);
        var controllersMissing = DailyUseDeviceReadiness.Unavailable(
            LtbDiagnosticCode.DevicesUnavailable,
            "left Quest 2 Touch controller is missing");
        var trackersMissing = DailyUseDeviceReadiness.Unavailable(
            LtbDiagnosticCode.DevicesUnavailable,
            "right physical tracker is missing");
        var vmtMissing = DailyUseDeviceReadiness.Unavailable(
            LtbDiagnosticCode.VmtUnavailable,
            "VMT Alive heartbeat is missing");
        context.Runtime.QueueDeviceReadiness(
            controllersMissing,
            controllersMissing,
            trackersMissing,
            trackersMissing,
            vmtMissing,
            vmtMissing);
        context.Runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 3);
        context.Runtime.CancelOnMonitorDelayNumber = 1;

        var result = await context.Coordinator.RunAsync(cancellation.Token);

        Assert.Equal(ReliableDailyUseStopReason.Cancellation, result.StopReason);
        Assert.Equal(2, context.Log.Events.Count(
            entry => entry.Code == LtbDiagnosticCode.DevicesUnavailable));
        Assert.Single(context.Log.Events.Where(
            entry => entry.Code == LtbDiagnosticCode.VmtUnavailable));
        Assert.Equal(6, context.Runtime.Delays.Count(delay => delay == ReconnectDelay));
        var firstApply = context.Runtime.Journal.FindIndex(entry =>
            entry.StartsWith("apply:", StringComparison.Ordinal));
        var readyProbe = context.Runtime.Journal.IndexOf("devices:3");
        Assert.True(readyProbe >= 0 && readyProbe < firstApply);
        Assert.DoesNotContain(
            context.Runtime.Journal.Take(readyProbe),
            entry => entry.StartsWith("apply:", StringComparison.Ordinal));
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task SteamVrStopRunsWatchdogSafeDisableAndEndsStopped()
    {
        var context = CreateContext();
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                "SteamVR emitted a terminal quit event"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SteamVrStopped, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.Stopped);
        Assert.Equal(MonitorInterval, Assert.Single(context.Runtime.Delays));
        Assert.True(
            context.Runtime.Journal.IndexOf($"delay:{MonitorInterval.TotalMilliseconds:R}:Active") <
            context.Runtime.Journal.IndexOf("health:3:2"));
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SteamVrStopped &&
                     entry.State == RuntimeApplicationState.Active);
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task UnhealthyImmediatelyAfterApplyNeverTransitionsActive()
    {
        var context = CreateContext();
        context.Runtime.QueueHealth(new RuntimeHealthSnapshot(
            RuntimeHealthFailureKind.SteamVrStopped,
            "SteamVR stopped while profiles were being committed"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SteamVrStopped, result.StopReason);
        Assert.DoesNotContain(RuntimeApplicationState.Active, result.StateHistory);
        var healthIndex = context.Runtime.Journal.IndexOf("health:3:1");
        var delayIndex = context.Runtime.Journal.FindIndex(entry => entry.StartsWith(
            "delay:",
            StringComparison.Ordinal));
        Assert.True(
            healthIndex >= 0 && (delayIndex < 0 || healthIndex < delayIndex),
            "Post-apply health must be checked before Active and before the first watchdog delay.");
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task SafeDisableAttemptsBothSurfacesForBothHandsAfterOneFailure()
    {
        var context = CreateContext();
        context.Runtime.FailDeactivateCall = 1;
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                "SteamVR stopped during failure-injection test"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SafeDisableFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.Single(result.SafeDisableFailures);
        Assert.Equal(
            [
                "deactivate:right:3",
                "release-override:right:3",
                "deactivate:left:3",
                "release-override:left:3",
            ],
            context.Runtime.Journal.Where(entry =>
                entry.StartsWith("deactivate:", StringComparison.Ordinal) ||
                entry.StartsWith("release-override:", StringComparison.Ordinal)));
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SafeDisableFailed);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
    }

    [Fact]
    public async Task SafeDisableBoundsHungCleanupAndContinuesEveryRemainingSurface()
    {
        var context = CreateContext();
        context.Runtime.HangDeactivateCall = 1;
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                "SteamVR stopped before bounded-cleanup test"));

        var run = context.Coordinator.RunAsync();
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(run, completed);
        var result = await run;
        Assert.Equal(ReliableDailyUseStopReason.SafeDisableFailed, result.StopReason);
        Assert.Contains("release-override:right:3", context.Runtime.Journal);
        Assert.Contains("deactivate:left:3", context.Runtime.Journal);
        Assert.Contains("release-override:left:3", context.Runtime.Journal);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
    }

    [Fact]
    public async Task UnexpectedMonitorExceptionIsLoggedBeforeEveryActiveSurfaceIsSafelyDisabled()
    {
        var context = CreateContext();
        context.Runtime.FailHealthCheckCall = 2;
        context.Runtime.QueueHealth(RuntimeHealthSnapshot.Healthy("post-apply health passed"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.RuntimeFailure, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        AssertContainsSequence(
            result.StateHistory,
            RuntimeApplicationState.Active,
            RuntimeApplicationState.SafeDisable,
            RuntimeApplicationState.Stopped);
        var failure = Assert.Single(context.Log.Events.Where(
            entry => entry.Code == LtbDiagnosticCode.RuntimeFailure));
        Assert.Equal(RuntimeApplicationState.Active, failure.State);
        Assert.Equal(LtbLogLevel.Error, failure.Level);
        Assert.Equal(2, failure.Properties.Count);
        Assert.Equal(nameof(IOException), failure.Properties["exceptionType"]);
        Assert.Equal(
            "synthetic monitor adapter failure",
            failure.Properties["exceptionMessage"]);
        Assert.Equal(
            [
                "deactivate:right:3",
                "release-override:right:3",
                "deactivate:left:3",
                "release-override:left:3",
            ],
            context.Runtime.Journal.Where(entry =>
                entry.StartsWith("deactivate:", StringComparison.Ordinal) ||
                entry.StartsWith("release-override:", StringComparison.Ordinal)));
        var runtimeFailureIndex = context.Log.Events.ToList().FindIndex(
            entry => entry.Code == LtbDiagnosticCode.RuntimeFailure);
        var safeDisableIndex = context.Log.Events.ToList().FindIndex(
            entry => entry.Code == LtbDiagnosticCode.SafeDisableStarted);
        var stoppedIndex = context.Log.Events.ToList().FindLastIndex(
            entry => entry.Code == LtbDiagnosticCode.StateTransition &&
                     entry.State == RuntimeApplicationState.Stopped &&
                     entry.Properties.TryGetValue("state", out var state) &&
                     state == nameof(RuntimeApplicationState.Stopped));
        Assert.True(
            runtimeFailureIndex >= 0 &&
            runtimeFailureIndex < safeDisableIndex &&
            runtimeFailureIndex < stoppedIndex,
            "RuntimeFailure must be recorded before bounded cleanup and the terminal transition.");
        AssertNoFrozenHand(context);
    }

    [Fact]
    public async Task UnexpectedMonitorExceptionReturnsSafeDisableFailedOnlyWhenCleanupFails()
    {
        var context = CreateContext();
        context.Runtime.FailHealthCheckCall = 2;
        context.Runtime.FailReleaseOverrideHand = CalibrationWizardHand.Right;
        context.Runtime.QueueHealth(RuntimeHealthSnapshot.Healthy("post-apply health passed"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SafeDisableFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.Single(result.SafeDisableFailures);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.RuntimeFailure &&
                     entry.Properties["exceptionType"] == nameof(IOException) &&
                     entry.Properties["exceptionMessage"] ==
                         "synthetic monitor adapter failure");
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SafeDisableFailed);
        Assert.Equal(
            [
                "deactivate:right:3",
                "release-override:right:3",
                "deactivate:left:3",
                "release-override:left:3",
            ],
            context.Runtime.Journal.Where(entry =>
                entry.StartsWith("deactivate:", StringComparison.Ordinal) ||
                entry.StartsWith("release-override:", StringComparison.Ordinal)));
        Assert.Empty(context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
    }

    [Fact]
    public async Task SettingsReleaseFailureIsReportedAfterEveryCleanupSurfaceIsAttempted()
    {
        var context = CreateContext();
        context.Runtime.FailReleaseOverrideHand = CalibrationWizardHand.Right;
        context.Runtime.QueueHealth(
            RuntimeHealthSnapshot.Healthy("post-apply health passed"),
            new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                "SteamVR stopped during settings-release failure injection"));

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.SafeDisableFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.Single(result.SafeDisableFailures);
        Assert.Equal(
            [
                "deactivate:right:3",
                "release-override:right:3",
                "deactivate:left:3",
                "release-override:left:3",
            ],
            context.Runtime.Journal.Where(entry =>
                entry.StartsWith("deactivate:", StringComparison.Ordinal) ||
                entry.StartsWith("release-override:", StringComparison.Ordinal)));
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.SafeDisableFailed);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
    }

    private static ReliableDailyUseContext CreateContext(
        CancellationTokenSource? cancellation = null,
        bool queueInitialDevices = true)
    {
        var time = new ReliableManualTimeProvider();
        var runtime = new ReliableDailyUseFakeRuntime(time, cancellation)
        {
            ExpectedMonitorInterval = MonitorInterval,
        };
        if (queueInitialDevices)
        {
            runtime.QueueDeviceSet(
                ReliableDailyUseFakeRuntime.MatchingDevices,
                transientHandle: 3);
        }

        var log = new InMemoryLtbLogSink();
        var coordinator = new ReliableDailyUseCoordinator(
            runtime,
            new ReliableProfileBackend(),
            log,
            new ReliableDailyUseOptions
            {
                MonitorInterval = MonitorInterval,
                ReconnectRetryDelay = ReconnectDelay,
                CleanupTimeout = TimeSpan.FromMilliseconds(41),
            },
            time);
        runtime.StateProvider = () => coordinator.CurrentState;
        return new ReliableDailyUseContext(coordinator, runtime, log);
    }

    private static void AssertNoFrozenHand(ReliableDailyUseContext context)
    {
        Assert.Equal(RuntimeApplicationState.Stopped, context.Coordinator.CurrentState);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
        Assert.Equal(0, context.Runtime.StaleApplyAttempts);
    }

    private static void AssertContainsSequence(
        IReadOnlyList<RuntimeApplicationState> actual,
        params RuntimeApplicationState[] expected)
    {
        var start = Enumerable.Range(0, actual.Count - expected.Length + 1)
            .FirstOrDefault(
                index => actual.Skip(index).Take(expected.Length).SequenceEqual(expected),
                -1);
        Assert.True(
            start >= 0,
            $"Expected contiguous state sequence [{string.Join(", ", expected)}], " +
            $"actual [{string.Join(", ", actual)}].");
    }

    private sealed record ReliableDailyUseContext(
        ReliableDailyUseCoordinator Coordinator,
        ReliableDailyUseFakeRuntime Runtime,
        InMemoryLtbLogSink Log);
}
