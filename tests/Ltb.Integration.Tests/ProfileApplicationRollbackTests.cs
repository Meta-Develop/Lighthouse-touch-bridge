using Ltb.App;
using Ltb.Core;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class ProfileApplicationRollbackTests
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(19);

    [Fact]
    public async Task SecondHandApplyFailureRollsBackEveryTouchedReceiptInReverseOrder()
    {
        var context = CreateContext();
        context.Runtime.FailApplyCall = 2;

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.ProfileApplyFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Ready, result.FinalState);
        Assert.DoesNotContain(RuntimeApplicationState.Active, result.StateHistory);
        Assert.Empty(result.RollbackFailures);
        Assert.Equal(
            [
                "deactivate:right:31",
                "rollback-override:right:31",
                "deactivate:left:31",
                "rollback-override:left:31",
            ],
            RollbackJournal(context.Runtime));
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.RollbackCompleted);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.ProfileApplyFailed);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
        Assert.Equal(0, context.Runtime.StaleApplyAttempts);
    }

    [Fact]
    public async Task RollbackFailureIsTypedStopsAndStillProcessesOlderReceipt()
    {
        var context = CreateContext();
        context.Runtime.FailApplyCall = 2;
        context.Runtime.FailRollbackOverrideHand = CalibrationWizardHand.Right;

        var result = await context.Coordinator.RunAsync();

        Assert.Equal(ReliableDailyUseStopReason.ProfileApplyFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.Single(result.RollbackFailures);
        Assert.Equal(
            [
                "deactivate:right:31",
                "rollback-override:right:31",
                "deactivate:left:31",
                "rollback-override:left:31",
            ],
            RollbackJournal(context.Runtime));
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.RollbackFailed &&
                     entry.Level == LtbLogLevel.Error);
        Assert.Empty(context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
    }

    [Fact]
    public void ExplicitRollbackNeverOverwritesAWriterAfterItsRecoveryPoint()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ltb-owned-rollback-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "steamvr.vrsettings");
        try
        {
            File.WriteAllText(settingsPath, "{\"TrackingOverrides\":{}}\n");
            var manager = new SteamVrSettingsManager(settingsPath);
            var recoveryPoint = manager.EnableOverride(new TrackingOverrideBinding(
                "/devices/vmt/VMT_1",
                TrackingOverrideBinding.LeftHandPath));
            var externalWinner = "{\"externalWinner\":true}\n";
            File.WriteAllText(settingsPath, externalWinner);

            _ = Assert.ThrowsAny<IOException>(() => manager.Rollback(recoveryPoint));

            Assert.Equal(externalWinner, File.ReadAllText(settingsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HungRollbackIsBoundedProcessesOlderReceiptAndNeverBecomesActive()
    {
        var context = CreateContext(TimeSpan.FromMilliseconds(41));
        context.Runtime.FailApplyCall = 2;
        context.Runtime.HangRollbackOverrideHand = CalibrationWizardHand.Right;

        var run = context.Coordinator.RunAsync();
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(run, completed);
        var result = await run;
        Assert.Equal(ReliableDailyUseStopReason.ProfileApplyFailed, result.StopReason);
        Assert.Equal(RuntimeApplicationState.Stopped, result.FinalState);
        Assert.DoesNotContain(RuntimeApplicationState.Active, result.StateHistory);
        Assert.Single(result.RollbackFailures);
        Assert.Equal(
            [
                "deactivate:right:31",
                "rollback-override:right:31",
                "deactivate:left:31",
                "rollback-override:left:31",
            ],
            RollbackJournal(context.Runtime));
        Assert.Equal(
            [CalibrationWizardHand.Right],
            context.Runtime.ActiveOverrideHands);
        Assert.Empty(context.Runtime.ActiveVmtHands);
        Assert.Contains(
            context.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.RollbackFailed);
    }

    private static RollbackContext CreateContext(TimeSpan? cleanupTimeout = null)
    {
        var time = new ReliableManualTimeProvider();
        var runtime = new ReliableDailyUseFakeRuntime(time)
        {
            ExpectedMonitorInterval = MonitorInterval,
        };
        runtime.QueueDeviceSet(
            ReliableDailyUseFakeRuntime.MatchingDevices,
            transientHandle: 31);
        var log = new InMemoryLtbLogSink();
        var coordinator = new ReliableDailyUseCoordinator(
            runtime,
            new ReliableProfileBackend(),
            log,
            new ReliableDailyUseOptions
            {
                MonitorInterval = MonitorInterval,
                ReconnectRetryDelay = TimeSpan.FromMilliseconds(71),
                CleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(2),
            },
            time);
        runtime.StateProvider = () => coordinator.CurrentState;
        return new RollbackContext(coordinator, runtime, log);
    }

    private static IReadOnlyList<string> RollbackJournal(
        ReliableDailyUseFakeRuntime runtime) =>
        runtime.Journal.Where(entry =>
            entry.StartsWith("deactivate:", StringComparison.Ordinal) ||
            entry.StartsWith("rollback-override:", StringComparison.Ordinal)).ToArray();

    private sealed record RollbackContext(
        ReliableDailyUseCoordinator Coordinator,
        ReliableDailyUseFakeRuntime Runtime,
        InMemoryLtbLogSink Log);
}
