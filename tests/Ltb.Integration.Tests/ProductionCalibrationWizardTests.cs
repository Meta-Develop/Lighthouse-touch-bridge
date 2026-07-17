using Ltb.App;
using Ltb.Core;

namespace Ltb.Integration.Tests;

public sealed class ProductionCalibrationWizardTests
{
    // Kept in source as the stable sample transcript promised by the v0.1
    // Definition-of-Done report; the end-to-end test asserts every line.
    private static readonly string[] SampleTranscript =
    [
        "state: DependencyCheck",
        "state: OverrideRelease",
        "state: Recording",
        "state: Association",
        "state: RotationSolve",
        "state: ApplyProfile",
        "state: Active",
        "profile: hand=left tracker=LHR-TEST0001 mode=full_6dof action=saved",
        "profile: hand=right tracker=LHR-TEST0002 mode=rotation_only action=saved",
    ];

    [Fact]
    public async Task ProductionCompositionRunsReleaseCaptureSolvePersistApplyHealthActive()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath);
        var output = new TimelineWizardOutput(timeline);
        var runtime = CreateRuntime(live);

        var result = await new TwoHandCalibrationWizard(
            runtime,
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            output).RunAsync();

        Assert.True(result.Success, result.Diagnostic);
        Assert.NotNull(runtime.ActiveLease);
        Assert.True(File.Exists(sandbox.ProfilePath));
        Assert.Equal(
            Enum.GetValues<CalibrationWizardHand>().OrderBy(hand => hand),
            live.ActiveVmtHands.OrderBy(hand => hand));
        Assert.Equal(
            Enum.GetValues<CalibrationWizardHand>().OrderBy(hand => hand),
            live.ActiveOverrideHands.OrderBy(hand => hand));
        AssertOrdered(
            timeline,
            "dependency:hmd-ready",
            "devices",
            "release:override:left",
            "release:vmt:left",
            "release:override:right",
            "release:vmt:right",
            "verify:original-touch",
            "capture:left",
            "capture:right",
            "state:Association",
            "state:RotationSolve",
            "persist",
            "apply:left",
            "apply:right",
            "health",
            "state:Active");
        foreach (var expected in SampleTranscript)
        {
            Assert.Contains(output.Transcript, line => line.StartsWith(
                expected,
                StringComparison.Ordinal));
        }

        using var watchdogStop = new CancellationTokenSource();
        live.CancelDelayWith = watchdogStop;
        var watchdog = new ReliableDailyUseCoordinator(
            live,
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            options: new ReliableDailyUseOptions
            {
                MonitorInterval = TimeSpan.FromMilliseconds(1),
                ReconnectRetryDelay = TimeSpan.FromMilliseconds(1),
                CleanupTimeout = TimeSpan.FromMilliseconds(100),
            });
        var monitored = await watchdog.MonitorActiveLeaseAsync(
            runtime.ActiveLease!,
            watchdogStop.Token);

        Assert.Equal(ReliableDailyUseStopReason.Cancellation, monitored.StopReason);
        Assert.Empty(monitored.SafeDisableFailures);
        Assert.Contains(RuntimeApplicationState.Active, monitored.StateHistory);
        Assert.Contains(RuntimeApplicationState.SafeDisable, monitored.StateHistory);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
    }

    [Fact]
    public async Task CancellationExceptionAfterReleaseRunsNonCancelledTwoHandCleanup()
    {
        using var sandbox = new WizardSandbox();
        using var cancellation = new CancellationTokenSource();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath)
        {
            CancelCaptureWith = cancellation,
        };
        var originalUnrelated = live.UnrelatedSettings.ToArray();
        var runtime = CreateRuntime(live);

        var result = await new TwoHandCalibrationWizard(
            runtime,
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            new TimelineWizardOutput(timeline)).RunAsync(cancellation.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Empty(result.CleanupFailures);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Contains(CalibrationWizardState.SafeDisable, result.StateHistory);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
        Assert.Equal(originalUnrelated, live.UnrelatedSettings);
        Assert.True(timeline.Count(entry => entry == "release:vmt:left") >= 2);
        Assert.True(timeline.Count(entry => entry == "release:override:right") >= 2);
    }

    [Fact]
    public async Task FirstHandApplyFailureRollsBackAndThenReleasesEveryCaptureSurface()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath)
        {
            FailApplyCall = 1,
        };
        var originalUnrelated = live.UnrelatedSettings.ToArray();
        var runtime = CreateRuntime(live);

        var result = await new TwoHandCalibrationWizard(
            runtime,
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            new TimelineWizardOutput(timeline)).RunAsync();

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Empty(result.CleanupFailures);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.DoesNotContain(CalibrationWizardState.Active, result.StateHistory);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
        Assert.Equal(originalUnrelated, live.UnrelatedSettings);
        AssertOrdered(
            timeline,
            "apply:left",
            "rollback:override:left",
            "rollback:deactivate:left",
            "state:SafeDisable");
        Assert.DoesNotContain("apply:right", timeline);
    }

    [Fact]
    public async Task AbortCleanupFailureIsDistinctAndStillAttemptsTheOtherHand()
    {
        using var sandbox = new WizardSandbox();
        using var cancellation = new CancellationTokenSource();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath)
        {
            CancelCaptureWith = cancellation,
            FailReleaseOverrideCall = 3,
        };

        var result = await new TwoHandCalibrationWizard(
            CreateRuntime(live),
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            new TimelineWizardOutput(timeline)).RunAsync(cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Single(result.CleanupFailures);
        Assert.Equal(CalibrationWizardState.SafeDisable, result.FinalState);
        Assert.Contains("manual state inspection", result.Diagnostic);
        Assert.True(timeline.Count(entry => entry == "release:override:right") >= 2);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
    }

    [Fact]
    public async Task UnexpectedActiveObserverFailureStillSafeDisablesRetainedLease()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath);

        var result = await new TwoHandCalibrationWizard(
            CreateRuntime(live),
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            new ThrowingActiveWizardOutput(timeline)).RunAsync();

        Assert.False(result.Success);
        Assert.Empty(result.CleanupFailures);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Contains("unexpected production-wizard operation", result.Diagnostic);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
    }

    [Fact]
    public async Task PostSuccessOutputFailureBeforeWatchdogHandoffSafeDisablesActiveLease()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var diagnostics = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath);
        var runtime = CreateRuntime(live);
        var monitorCalled = false;

        var exitCode = await Program.RunProductionWizardActiveLifecycleAsync(
            new TwoHandCalibrationWizard(
                runtime,
                new FileCalibrationWizardBackend(sandbox.ProfilePath),
                new TimelineWizardOutput(timeline)),
            runtime,
            (_, _) =>
            {
                monitorCalled = true;
                throw new InvalidOperationException("watchdog must not receive ownership");
            },
            result =>
            {
                Assert.True(result.Success, result.Diagnostic);
                throw new IOException("synthetic post-success output failure");
            },
            _ => throw new InvalidOperationException("watchdog result was not expected"),
            diagnostics.Add);

        Assert.Equal(2, exitCode);
        Assert.False(monitorCalled);
        Assert.Null(runtime.ActiveLease);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
        Assert.Contains(
            diagnostics,
            message => message.Contains(
                "fallback SafeDisable completed",
                StringComparison.Ordinal));
        AssertOrdered(
            timeline,
            "state:Active",
            "safe-disable:override:right",
            "safe-disable:override:left");
    }

    [Fact]
    public async Task PostSuccessOutputFailureReturnsFourWhenFallbackCleanupReportsFailure()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath)
        {
            FailSafeDisableOverrideHand = CalibrationWizardHand.Left,
        };
        var runtime = CreateRuntime(live);

        var exitCode = await Program.RunProductionWizardActiveLifecycleAsync(
            new TwoHandCalibrationWizard(
                runtime,
                new FileCalibrationWizardBackend(sandbox.ProfilePath),
                new TimelineWizardOutput(timeline)),
            runtime,
            (_, _) => throw new InvalidOperationException(
                "watchdog must not receive ownership"),
            result =>
            {
                Assert.True(result.Success, result.Diagnostic);
                throw new IOException("synthetic post-success output failure");
            },
            _ => throw new InvalidOperationException("watchdog result was not expected"),
            _ => { });

        Assert.Equal(4, exitCode);
        Assert.Null(runtime.ActiveLease);
        Assert.Empty(live.ActiveVmtHands);
        Assert.Empty(live.ActiveOverrideHands);
        Assert.Contains("safe-disable:override:right", timeline);
        Assert.Contains("safe-disable:override:left", timeline);
    }

    [Fact]
    public async Task ActiveQuestHmdBlocksProductionAdapterBeforeAnyMutation()
    {
        using var sandbox = new WizardSandbox();
        var timeline = new List<string>();
        var live = new FakeProductionWizardBackend(timeline, sandbox.ProfilePath)
        {
            DependencyStatus = new CalibrationWizardDependencyStatus(
                true,
                true,
                "Quest/ALVR is the active SteamVR display HMD; use tracking-reference-only " +
                "mode and make a Lighthouse HMD active.",
                ActiveHmdReady: false),
        };

        var result = await new TwoHandCalibrationWizard(
            CreateRuntime(live),
            new FileCalibrationWizardBackend(sandbox.ProfilePath),
            new TimelineWizardOutput(timeline)).RunAsync();

        Assert.False(result.Success);
        Assert.Contains("tracking-reference-only", result.Diagnostic);
        Assert.Equal(["state:DependencyCheck", "dependency:hmd-blocked", "state:Ready"], timeline);
        Assert.Empty(live.MutationJournal);
    }

    private static ProductionCalibrationWizardRuntime CreateRuntime(
        FakeProductionWizardBackend backend) =>
        new(
            backend,
            new ProductionCalibrationWizardOptions
            {
                CaptureDurationSeconds = 4d,
                CaptureRateHz = 90d,
                DeviceRetryDelay = TimeSpan.FromMilliseconds(1),
                CleanupTimeout = TimeSpan.FromMilliseconds(100),
            });

    private static void AssertOrdered(IReadOnlyList<string> actual, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var index = actual.ToList().FindIndex(previous + 1, value => value == item);
            Assert.True(
                index > previous,
                $"Expected '{item}' after index {previous}. Timeline: {string.Join(" | ", actual)}");
            previous = index;
        }
    }

    private sealed class TimelineWizardOutput : ICalibrationWizardOutput
    {
        private readonly List<string> _timeline;

        public TimelineWizardOutput(List<string> timeline)
        {
            _timeline = timeline;
        }

        public List<string> Transcript { get; } = [];

        public void OnStateChanged(CalibrationWizardState state, string diagnostic)
        {
            _timeline.Add($"state:{state}");
            Transcript.Add($"state: {state} ({diagnostic})");
        }

        public void OnCaptureProgress(CalibrationWizardCaptureProgress progress) =>
            Transcript.Add($"coverage: hand={progress.Hand.ToString().ToLowerInvariant()}");

        public void WriteLine(string message) => Transcript.Add(message);
    }

    private sealed class ThrowingActiveWizardOutput : ICalibrationWizardOutput
    {
        private readonly List<string> _timeline;

        public ThrowingActiveWizardOutput(List<string> timeline)
        {
            _timeline = timeline;
        }

        public void OnStateChanged(CalibrationWizardState state, string diagnostic)
        {
            _timeline.Add($"state:{state}");
            if (state == CalibrationWizardState.Active)
            {
                throw new IOException("synthetic Active observer failure");
            }
        }

        public void OnCaptureProgress(CalibrationWizardCaptureProgress progress)
        {
        }

        public void WriteLine(string message)
        {
        }
    }

    private sealed class WizardSandbox : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-production-wizard-{Guid.NewGuid():N}");

        public WizardSandbox()
        {
            Directory.CreateDirectory(_directory);
            ProfilePath = Path.Combine(_directory, "profiles.json");
        }

        public string ProfilePath { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

internal sealed class FakeProductionWizardBackend : IProductionCalibrationWizardBackend
{
    private readonly List<string> _timeline;
    private readonly string _profilePath;
    private readonly Dictionary<Guid, CalibrationWizardHand> _applications = [];
    private int _applyCalls;
    private int _releaseOverrideCalls;

    public FakeProductionWizardBackend(List<string> timeline, string profilePath)
    {
        _timeline = timeline;
        _profilePath = profilePath;
        ActiveVmtHands.UnionWith(Enum.GetValues<CalibrationWizardHand>());
        ActiveOverrideHands.UnionWith(Enum.GetValues<CalibrationWizardHand>());
    }

    public CalibrationWizardDependencyStatus DependencyStatus { get; set; } =
        new(true, true, "ALVR, VMT, and active Lighthouse HMD ready", true);

    public CancellationTokenSource? CancelCaptureWith { get; init; }

    public CancellationTokenSource? CancelDelayWith { get; set; }

    public int? FailApplyCall { get; init; }

    public int? FailReleaseOverrideCall { get; init; }

    public CalibrationWizardHand? FailSafeDisableOverrideHand { get; init; }

    public HashSet<CalibrationWizardHand> ActiveVmtHands { get; } = [];

    public HashSet<CalibrationWizardHand> ActiveOverrideHands { get; } = [];

    public List<KeyValuePair<string, string>> UnrelatedSettings { get; } =
    [
        new("dashboard", "preserve"),
        new("/devices/unrelated/source", "/user/head"),
    ];

    public List<string> MutationJournal { get; } = [];

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _timeline.Add(DependencyStatus.IsReady
            ? "dependency:hmd-ready"
            : "dependency:hmd-blocked");
        return Task.FromResult(DependencyStatus);
    }

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _timeline.Add("steamvr");
        return Task.CompletedTask;
    }

    public Task<DailyUseDeviceReadiness> ProbeDeviceReadinessAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _timeline.Add("devices");
        return Task.FromResult(DailyUseDeviceReadiness.Ready(
            ScriptedCalibrationWizardRuntime.Devices));
    }

    public Task DeactivateWizardVmtAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = $"release:vmt:{Format(hand)}";
        _timeline.Add(entry);
        MutationJournal.Add(entry);
        ActiveVmtHands.Remove(hand);
        return Task.CompletedTask;
    }

    public Task ReleaseWizardHandOverrideAsync(
        CalibrationWizardHand hand,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = $"release:override:{Format(hand)}";
        _timeline.Add(entry);
        MutationJournal.Add(entry);
        ActiveOverrideHands.Remove(hand);
        _releaseOverrideCalls++;
        if (_releaseOverrideCalls == FailReleaseOverrideCall)
        {
            throw new IOException($"synthetic {hand} semantic release failure");
        }

        return Task.CompletedTask;
    }

    public Task VerifyOriginalTouchPosesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Empty(ActiveVmtHands);
        Assert.Empty(ActiveOverrideHands);
        _timeline.Add("verify:original-touch");
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardCapture> CaptureWizardHandAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        double durationSeconds,
        double sampleRateHz,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Empty(ActiveVmtHands);
        Assert.Empty(ActiveOverrideHands);
        _timeline.Add($"capture:{Format(hand)}");
        if (CancelCaptureWith is not null)
        {
            CancelCaptureWith.Cancel();
            throw new OperationCanceledException(CancelCaptureWith.Token);
        }

        return Task.FromResult(ScriptedWizardCaptureFactory.Create(hand));
    }

    public DailyUseProfileApplication CreateProfileApplication(
        CalibrationWizardProfileView profile)
    {
        if (!_timeline.Contains("persist", StringComparer.Ordinal))
        {
            Assert.True(File.Exists(_profilePath));
            _timeline.Add("persist");
        }

        var application = new DailyUseProfileApplication(profile);
        _applications.Add(application.OperationId, profile.Hand);
        return application;
    }

    public Task ApplyProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hand = Hand(application);
        _applyCalls++;
        _timeline.Add($"apply:{Format(hand)}");
        ActiveVmtHands.Add(hand);
        ActiveOverrideHands.Add(hand);
        if (_applyCalls == FailApplyCall)
        {
            throw new IOException($"synthetic {hand} apply failure after effects");
        }

        return Task.CompletedTask;
    }

    public Task DeactivateProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var hand = Hand(application);
        _timeline.Add($"rollback:deactivate:{Format(hand)}");
        ActiveVmtHands.Remove(hand);
        _applications.Remove(application.OperationId);
        return Task.CompletedTask;
    }

    public Task RollbackProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var hand = Hand(application);
        _timeline.Add($"rollback:override:{Format(hand)}");
        ActiveOverrideHands.Remove(hand);
        return Task.CompletedTask;
    }

    public Task ReleaseProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var hand = Hand(application);
        _timeline.Add($"safe-disable:override:{Format(hand)}");
        ActiveOverrideHands.Remove(hand);
        if (hand == FailSafeDisableOverrideHand)
        {
            throw new IOException($"synthetic {hand} SafeDisable override failure");
        }

        return Task.CompletedTask;
    }

    public Task<RuntimeHealthSnapshot> CheckHealthAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(2, ActiveVmtHands.Count);
        Assert.Equal(2, ActiveOverrideHands.Count);
        _timeline.Add("health");
        return Task.FromResult(RuntimeHealthSnapshot.Healthy());
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (CancelDelayWith is not null)
        {
            CancelDelayWith.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private CalibrationWizardHand Hand(DailyUseProfileApplication application) =>
        _applications[application.OperationId];

    private static string Format(CalibrationWizardHand hand) =>
        hand.ToString().ToLowerInvariant();
}
