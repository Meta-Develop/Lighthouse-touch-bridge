using Ltb.App;
using Ltb.Core;

namespace Ltb.Gui.Tests;

public sealed class ProductionCalibrationWizardSessionTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("ltb-gui-production-session-").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public async Task SharedProductionCompositionRunsThroughInjectedFakesAndSafeDisables()
    {
        using var stop = new CancellationTokenSource();
        var timeline = new List<string>();
        var backend = new FakeProductionBackend(timeline) { MonitorStop = stop };
        var output = new TimelineOutput(timeline);
        await using var session = ProductionCalibrationWizardSessionFactory.CreateForBackend(
            Options(),
            backend);

        var result = await session.RunAsync(output, stop.Token);
        var guiResult = result.ToWizardResult();

        Assert.Equal(ProductionCalibrationWizardStopReason.Cancellation, result.StopReason);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.WizardResult.Success, result.WizardResult.Diagnostic);
        Assert.True(guiResult.Success, guiResult.Diagnostic);
        Assert.Equal(CalibrationWizardState.Stopped, guiResult.FinalState);
        Assert.True(File.Exists(Options().ProfileStorePath));
        Assert.Empty(backend.ActiveVmtHands);
        Assert.Empty(backend.ActiveOverrideHands);
        AssertOrdered(
            timeline,
            "dependency",
            "devices",
            "release:override:left",
            "release:override:right",
            "release:vmt:left",
            "release:vmt:right",
            "capture:left",
            "capture:right",
            "apply:left",
            "apply:right",
            "state:Active",
            "safe-disable:override:right",
            "safe-disable:override:left",
            "state:Stopped");
    }

    [Fact]
    public async Task UnavailableNativeBoundaryReturnsBoundedDiagnosticWithoutMutation()
    {
        var timeline = new List<string>();
        var backend = new FakeProductionBackend(timeline)
        {
            DependencyStatus = new CalibrationWizardDependencyStatus(
                false,
                true,
                "SteamVR/OpenVR is unavailable; start SteamVR and retry.",
                ActiveHmdReady: false),
        };
        await using var session = ProductionCalibrationWizardSessionFactory.CreateForBackend(
            Options(),
            backend);

        var result = await session.RunAsync(new TimelineOutput(timeline));

        Assert.Equal(ProductionCalibrationWizardStopReason.WizardFailed, result.StopReason);
        Assert.Equal(2, result.ExitCode);
        Assert.False(result.WizardResult.Success);
        Assert.Contains("SteamVR/OpenVR is unavailable", result.Diagnostic);
        Assert.Empty(backend.MutationJournal);
        Assert.Equal(["state:DependencyCheck", "dependency", "state:Ready"], timeline);
    }

    [Fact]
    public async Task CancellationBeforeActivePreservesCleanCancellationResult()
    {
        using var stop = new CancellationTokenSource();
        var timeline = new List<string>();
        var backend = new FakeProductionBackend(timeline)
        {
            DependencyStop = stop,
        };
        await using var session = ProductionCalibrationWizardSessionFactory.CreateForBackend(
            Options(),
            backend);

        var result = await session.RunAsync(new TimelineOutput(timeline), stop.Token);

        Assert.Equal(ProductionCalibrationWizardStopReason.Cancellation, result.StopReason);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.WizardResult.Cancelled);
        Assert.False(result.WizardResult.Success);
        Assert.Empty(result.CleanupFailures);
        Assert.Empty(result.WizardResult.CleanupFailures);
        Assert.Empty(backend.MutationJournal);
        Assert.Equal(["state:DependencyCheck", "dependency", "state:Ready"], timeline);
    }

    [Theory]
    [InlineData(-1, 1, "between 0 and 57")]
    [InlineData(58, 1, "between 0 and 57")]
    [InlineData(4, 4, "must be distinct")]
    public void ProductionOptionsRejectInvalidSlots(
        int left,
        int right,
        string expectedDiagnostic)
    {
        var options = Options() with { LeftVmtSlot = left, RightVmtSlot = right };

        Assert.False(options.TryValidate(out var diagnostic));
        Assert.Contains(expectedDiagnostic, diagnostic);
    }

    [Fact]
    public void ProductionOptionsValidateEveryRequiredNumericBoundary()
    {
        var cases = new (ProductionCalibrationWizardSessionOptions Options, string Expected)[]
        {
            (Options() with { ProfileStorePath = string.Empty }, "--profiles"),
            (Options() with { SteamVrSettingsPath = string.Empty }, "--steamvr-settings"),
            (Options() with { CaptureDurationSeconds = 0d }, "--duration"),
            (Options() with { CaptureDurationSeconds = 3_600.1d }, "--duration"),
            (Options() with { CaptureRateHz = double.NaN }, "--rate"),
            (Options() with { CaptureRateHz = 1_000.1d }, "--rate"),
            (Options() with { MonitorRateHz = 0d }, "--monitor-rate"),
            (Options() with { MonitorRateHz = double.PositiveInfinity }, "--monitor-rate"),
            (Options() with { ReconnectDelaySeconds = 0d }, "--reconnect-delay"),
            (Options() with { ReconnectDelaySeconds = 300.1d }, "--reconnect-delay"),
        };

        foreach (var testCase in cases)
        {
            Assert.False(testCase.Options.TryValidate(out var diagnostic));
            Assert.Contains(testCase.Expected, diagnostic);
        }
    }

    private ProductionCalibrationWizardSessionOptions Options() => new()
    {
        ProfileStorePath = Path.Combine(_tempDirectory, "profiles.json"),
        LeftVmtSlot = 0,
        RightVmtSlot = 1,
        SteamVrSettingsPath = Path.Combine(_tempDirectory, "steamvr.vrsettings"),
        CaptureDurationSeconds = 4d,
        CaptureRateHz = 90d,
        MonitorRateHz = 100d,
        ReconnectDelaySeconds = 0.001d,
    };

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

    private sealed class TimelineOutput : ICalibrationWizardOutput
    {
        private readonly List<string> _timeline;

        public TimelineOutput(List<string> timeline)
        {
            _timeline = timeline;
        }

        public void OnStateChanged(CalibrationWizardState state, string diagnostic) =>
            _timeline.Add($"state:{state}");

        public void OnCaptureProgress(CalibrationWizardCaptureProgress progress)
        {
        }

        public void WriteLine(string message)
        {
        }
    }

    private sealed class FakeProductionBackend : IProductionCalibrationWizardBackend
    {
        private readonly List<string> _timeline;
        private readonly Dictionary<Guid, CalibrationWizardHand> _applications = [];

        public FakeProductionBackend(List<string> timeline)
        {
            _timeline = timeline;
            ActiveVmtHands.UnionWith(Enum.GetValues<CalibrationWizardHand>());
            ActiveOverrideHands.UnionWith(Enum.GetValues<CalibrationWizardHand>());
        }

        public CalibrationWizardDependencyStatus DependencyStatus { get; init; } =
            new(true, true, "dependencies ready", true);

        public CancellationTokenSource? MonitorStop { get; init; }

        public CancellationTokenSource? DependencyStop { get; init; }

        public HashSet<CalibrationWizardHand> ActiveVmtHands { get; } = [];

        public HashSet<CalibrationWizardHand> ActiveOverrideHands { get; } = [];

        public List<string> MutationJournal { get; } = [];

        public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _timeline.Add("dependency");
            if (DependencyStop is not null)
            {
                DependencyStop.Cancel();
                throw new OperationCanceledException(DependencyStop.Token);
            }

            return Task.FromResult(DependencyStatus);
        }

        public Task WaitForSteamVrAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            RecordMutation($"release:vmt:{Format(hand)}");
            ActiveVmtHands.Remove(hand);
            return Task.CompletedTask;
        }

        public Task ReleaseWizardHandOverrideAsync(
            CalibrationWizardHand hand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordMutation($"release:override:{Format(hand)}");
            ActiveOverrideHands.Remove(hand);
            return Task.CompletedTask;
        }

        public Task VerifyOriginalTouchPosesAsync(
            CalibrationWizardDeviceSet devices,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Empty(ActiveVmtHands);
            Assert.Empty(ActiveOverrideHands);
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
            _timeline.Add($"capture:{Format(hand)}");
            return Task.FromResult(ScriptedWizardCaptureFactory.Create(hand));
        }

        public DailyUseProfileApplication CreateProfileApplication(
            CalibrationWizardProfileView profile)
        {
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
            _timeline.Add($"apply:{Format(hand)}");
            ActiveVmtHands.Add(hand);
            ActiveOverrideHands.Add(hand);
            return Task.CompletedTask;
        }

        public Task DeactivateProfileAsync(
            DailyUseProfileApplication application,
            CancellationToken cancellationToken)
        {
            var hand = Hand(application);
            ActiveVmtHands.Remove(hand);
            _applications.Remove(application.OperationId);
            return Task.CompletedTask;
        }

        public Task RollbackProfileOverrideAsync(
            DailyUseProfileApplication application,
            CancellationToken cancellationToken)
        {
            ActiveOverrideHands.Remove(Hand(application));
            return Task.CompletedTask;
        }

        public Task ReleaseProfileOverrideAsync(
            DailyUseProfileApplication application,
            CancellationToken cancellationToken)
        {
            var hand = Hand(application);
            _timeline.Add($"safe-disable:override:{Format(hand)}");
            ActiveOverrideHands.Remove(hand);
            return Task.CompletedTask;
        }

        public Task<RuntimeHealthSnapshot> CheckHealthAsync(
            IReadOnlyList<DailyUseProfileApplication> activeApplications,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RuntimeHealthSnapshot.Healthy());
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            MonitorStop?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private CalibrationWizardHand Hand(DailyUseProfileApplication application) =>
            _applications[application.OperationId];

        private void RecordMutation(string mutation)
        {
            _timeline.Add(mutation);
            MutationJournal.Add(mutation);
        }

        private static string Format(CalibrationWizardHand hand) =>
            hand.ToString().ToLowerInvariant();
    }
}
