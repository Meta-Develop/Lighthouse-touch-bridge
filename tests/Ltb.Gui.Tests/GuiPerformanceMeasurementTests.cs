using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ltb.App;
using Ltb.Driver;
using Ltb.Gui.Controls;
using Ltb.Gui.ViewModels;
using Ltb.MetaLink;
using Ltb.Protocol;
using Xunit.Abstractions;

namespace Ltb.Gui.Tests;

/// <summary>
/// Repeatable presentation measurements. The normal suite leaves this surface
/// dormant; opt in with:
/// LTB_GUI_PERF_MEASURE=1 dotnet test tests/Ltb.Gui.Tests/Ltb.Gui.Tests.csproj
/// -c Release --filter FullyQualifiedName~GuiPerformanceMeasurementTests
/// --logger "console;verbosity=detailed"
///
/// Results are Release/Linux-headless measurements. Window construction ends
/// after Show, UpdateLayout, and queued dispatcher jobs; it is not Windows
/// first paint. Live SteamVR, visual/DPI/accessibility review, and hardware are
/// intentionally outside this measurement boundary.
/// </summary>
public sealed class GuiPerformanceMeasurementTests(ITestOutputHelper output)
{
    private const string OptInVariable = "LTB_GUI_PERF_MEASURE";
    private const int ConstructionIterations = 500;
    private const int SnapshotIterations = 2_000;

    [AvaloniaFact]
    public async Task ReportReleaseLinuxHeadlessPresentationMeasurements()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                $"Measurement skipped unless {OptInVariable}=1. " +
                "No noisy timing threshold is asserted by the normal test suite.");
            return;
        }

        output.WriteLine(
            $"boundary=Release/Linux-headless runtime={Environment.Version} " +
            $"os={Environment.OSVersion} processors={Environment.ProcessorCount}");
        output.WriteLine(
            "unchecked=Windows-first-paint,live-SteamVR,visual-DPI-accessibility,hardware");

        MeasureViewModelConstruction();
        await MeasureSnapshotPresentationAsync();
        await MeasureRefreshCallerResponsivenessAsync();
        MeasureHeadlessWindowAndPlots();
    }

    private void MeasureViewModelConstruction()
    {
        for (var index = 0; index < 20; index++)
        {
            NewIdleViewModel()
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        var instances = new InternalDriverViewModel[ConstructionIterations];
        ForceGc();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var dispatches = 0;
        for (var index = 0; index < instances.Length; index++)
        {
            instances[index] = new InternalDriverViewModel(
                new NeverSessionFactory(),
                action =>
                {
                    dispatches++;
                    action();
                },
                timeSource: new ManualTimeSource(),
                delayScheduler: new ManualDelayScheduler());
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        output.WriteLine(
            $"viewmodel_construction iterations={instances.Length} " +
            $"total_ms={stopwatch.Elapsed.TotalMilliseconds:F3} " +
            $"mean_us={stopwatch.Elapsed.TotalMicroseconds / instances.Length:F3} " +
            $"allocated_bytes={allocated} " +
            $"bytes_per_iteration={(double)allocated / instances.Length:F1} " +
            $"dispatches={dispatches}");

        foreach (var instance in instances)
        {
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task MeasureSnapshotPresentationAsync()
    {
        var time = new ManualTimeSource();
        var scheduler = new ManualDelayScheduler();
        var session = new ControlledSession(Snapshot(42));
        var dispatches = 0;
        await using var viewModel = new InternalDriverViewModel(
            new SingleSessionFactory(session),
            action =>
            {
                dispatches++;
                action();
            },
            timeSource: time,
            delayScheduler: scheduler);

        var run = viewModel.StartAsync();
        await session.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var notifications = new NotificationCounts();
        viewModel.PropertyChanged += notifications.OnViewModelChanged;
        viewModel.LeftHand.PropertyChanged += notifications.OnLeftChanged;
        viewModel.RightHand.PropertyChanged += notifications.OnRightChanged;
        viewModel.DebugDiagnostics.PropertyChanged += notifications.OnDiagnosticsChanged;
        foreach (var row in viewModel.ReadinessRows)
        {
            row.PropertyChanged += notifications.OnReadinessRowChanged;
        }

        var snapshots = Enumerable.Range(0, SnapshotIterations)
            .Select(index => Snapshot((ulong)(1_000 + index)))
            .ToArray();

        dispatches = 0;
        notifications.Reset();
        ForceGc();
        var burstAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var burstWatch = Stopwatch.StartNew();
        foreach (var snapshot in snapshots)
        {
            session.Publish(snapshot);
        }

        burstWatch.Stop();
        var burstAllocated =
            GC.GetAllocatedBytesForCurrentThread() - burstAllocationStart;
        output.WriteLine(
            $"active_equivalent_burst snapshots={snapshots.Length} " +
            $"total_ms={burstWatch.Elapsed.TotalMilliseconds:F3} " +
            $"allocated_bytes={burstAllocated} " +
            $"bytes_per_snapshot={(double)burstAllocated / snapshots.Length:F1} " +
            $"ui_dispatches={dispatches} vm_notifications={notifications.ViewModel} " +
            $"left_notifications={notifications.Left} " +
            $"right_notifications={notifications.Right} " +
            $"readiness_row_notifications={notifications.ReadinessRows} " +
            $"scheduled_flushes={scheduler.ScheduleCalls}");
        Assert.Equal(0, dispatches);
        Assert.Equal(0, notifications.TotalPresentation);
        Assert.Equal(1, scheduler.ScheduleCalls);

        time.Advance(SnapshotPresentationCoalescer.ActivePresentationInterval);
        scheduler.RunPending();
        output.WriteLine(
            $"active_trailing_flush ui_dispatches={dispatches} " +
            $"vm_notifications={notifications.ViewModel} " +
            $"left_notifications={notifications.Left} " +
            $"right_notifications={notifications.Right} " +
            $"readiness_row_notifications={notifications.ReadinessRows}");
        Assert.Equal(1, dispatches);

        dispatches = 0;
        notifications.Reset();
        ForceGc();
        var presentedAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var presentedWatch = Stopwatch.StartNew();
        foreach (var snapshot in snapshots)
        {
            time.Advance(SnapshotPresentationCoalescer.ActivePresentationInterval);
            session.Publish(snapshot);
        }

        presentedWatch.Stop();
        var presentedAllocated =
            GC.GetAllocatedBytesForCurrentThread() - presentedAllocationStart;
        output.WriteLine(
            $"active_presented_10hz snapshots={snapshots.Length} " +
            $"total_ms={presentedWatch.Elapsed.TotalMilliseconds:F3} " +
            $"allocated_bytes={presentedAllocated} " +
            $"bytes_per_snapshot={(double)presentedAllocated / snapshots.Length:F1} " +
            $"ui_dispatches={dispatches} vm_notifications={notifications.ViewModel} " +
            $"left_notifications={notifications.Left} " +
            $"right_notifications={notifications.Right} " +
            $"readiness_row_notifications={notifications.ReadinessRows}");
        Assert.Equal(snapshots.Length, dispatches);
        Assert.Equal(0, notifications.Left);
        Assert.Equal(0, notifications.Right);
        Assert.Equal(0, notifications.ReadinessRows);

        viewModel.IsDebugEnabled = true;
        dispatches = 0;
        notifications.Reset();
        ForceGc();
        var diagnosticsAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var diagnosticsWatch = Stopwatch.StartNew();
        foreach (var snapshot in snapshots)
        {
            time.Advance(SnapshotPresentationCoalescer.ActivePresentationInterval);
            session.Publish(snapshot);
        }

        diagnosticsWatch.Stop();
        var diagnosticsAllocated =
            GC.GetAllocatedBytesForCurrentThread() - diagnosticsAllocationStart;
        output.WriteLine(
            $"diagnostics_presented_10hz samples={snapshots.Length} " +
            $"total_ms={diagnosticsWatch.Elapsed.TotalMilliseconds:F3} " +
            $"allocated_bytes={diagnosticsAllocated} " +
            $"bytes_per_sample={(double)diagnosticsAllocated / snapshots.Length:F1} " +
            $"ui_dispatches={dispatches} vm_notifications={notifications.ViewModel} " +
            $"diagnostic_notifications={notifications.Diagnostics} " +
            $"ring_cap={DebugDiagnosticsViewModel.MaximumSamples} " +
            $"retained={viewModel.DebugDiagnostics.RetainedSampleCount}");
        Assert.Equal(snapshots.Length, dispatches);
        Assert.Equal(DebugDiagnosticsViewModel.MaximumSamples, viewModel.DebugDiagnostics.RetainedSampleCount);

        session.AllowStop();
        await viewModel.StopAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task MeasureRefreshCallerResponsivenessAsync()
    {
        var control = new SynchronousPrefixControl(TimeSpan.FromMilliseconds(25));
        await using var viewModel = new TrackerBindingViewModel(
            control,
            action => action());
        var callerThread = Environment.CurrentManagedThreadId;

        var stopwatch = Stopwatch.StartNew();
        var refresh = viewModel.RefreshAsync();
        stopwatch.Stop();
        var completedOnReturn = refresh.IsCompleted;
        await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var controlThread = control.EntryThreadId;
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        output.WriteLine(
            $"refresh_sync_prefix simulated_ms=25 " +
            $"call_return_ms={stopwatch.Elapsed.TotalMilliseconds:F3} " +
            $"completed_on_return={completedOnReturn} " +
            $"caller_thread={callerThread} control_thread={controlThread}");
        Assert.False(completedOnReturn);
        Assert.NotEqual(callerThread, controlThread);
    }

    private void MeasureHeadlessWindowAndPlots()
    {
        var cold = MeasureOneWindow();
        var warmups = Enumerable.Range(0, 3)
            .Select(_ => MeasureOneWindow())
            .ToArray();
        foreach (var warmup in warmups)
        {
            Close(warmup);
        }

        const int iterations = 20;
        var elapsed = new double[iterations];
        var allocated = new long[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var measurement = MeasureOneWindow();
            elapsed[index] = measurement.ElapsedMilliseconds;
            allocated[index] = measurement.AllocatedBytes;
            Close(measurement);
        }

        Array.Sort(elapsed);
        Array.Sort(allocated);
        output.WriteLine(
            $"headless_window_show_layout_proxy cold_ms={cold.ElapsedMilliseconds:F3} " +
            $"cold_allocated_bytes={cold.AllocatedBytes} controls={cold.ControlCount} " +
            $"plots={cold.PlotCount} warmup=3 iterations={iterations} " +
            $"warm_median_ms={Percentile(elapsed, 0.50):F3} " +
            $"warm_p95_ms={Percentile(elapsed, 0.95):F3} " +
            $"warm_median_allocated_bytes={Percentile(allocated, 0.50):F0}");

        var plotInvalidations = 0;
        foreach (var plot in cold.Window
                     .GetVisualDescendants()
                     .OfType<DiagnosticsPlot>())
        {
            plot.PropertyChanged += (_, change) =>
            {
                if (change.Property == DiagnosticsPlot.RefreshVersionProperty)
                {
                    plotInvalidations++;
                }
            };
        }

        cold.ViewModel.IsDebugEnabled = true;
        Dispatcher.UIThread.RunJobs();
        var enableInvalidations = plotInvalidations;
        plotInvalidations = 0;
        const int plotSamples = 100;
        var sample = Snapshot(99);
        for (var index = 0; index < plotSamples; index++)
        {
            _ = cold.ViewModel.DebugDiagnostics.TrySample(sample, force: true);
            Dispatcher.UIThread.RunJobs();
        }

        output.WriteLine(
            $"diagnostics_plot_proxy samples={plotSamples} plots={cold.PlotCount} " +
            $"enable_refresh_version_changes={enableInvalidations} " +
            $"refresh_version_changes={plotInvalidations} " +
            $"expected={plotSamples * cold.PlotCount}");
        Assert.Equal(plotSamples * cold.PlotCount, plotInvalidations);
        Close(cold);
    }

    private static WindowMeasurement MeasureOneWindow()
    {
        ForceGc();
        var viewModel = NewIdleViewModel();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        return new WindowMeasurement(
            window,
            viewModel,
            stopwatch.Elapsed.TotalMilliseconds,
            allocated,
            window.GetVisualDescendants().OfType<Control>().Count(),
            window.GetVisualDescendants().OfType<DiagnosticsPlot>().Count());
    }

    private static void Close(WindowMeasurement measurement)
    {
        measurement.Window.Close();
        measurement.ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static InternalDriverViewModel NewIdleViewModel() =>
        new(
            new NeverSessionFactory(),
            action => action(),
            timeSource: new ManualTimeSource(),
            delayScheduler: new ManualDelayScheduler());

    private static InternalDriverSessionSnapshot Snapshot(ulong sequence)
    {
        var readiness = new InternalDriverSessionReadiness(
            PlatformSupported: true,
            SteamVrRunning: true,
            MetaBothHandsReady: true,
            TwoDistinctTrackersReady: true,
            ProfilesReady: true,
            DriverRegistered: true,
            DriverLoaded: true,
            FeedReady: true);
        var left = new InternalDriverHandSnapshot(
            ProtocolHand.Left,
            "LHR-LEFT",
            TrackerConnected: true,
            TrackerTracked: true,
            MetaLinkReadiness.Ready,
            MetaInputsValid: true,
            InternalDriverProfileReadiness.Calibrated,
            PoseAge: TimeSpan.FromMilliseconds(7),
            IsPublishing: true,
            InternalDriverNeutralReason.None,
            "Left ready.");
        var right = new InternalDriverHandSnapshot(
            ProtocolHand.Right,
            "LHR-RIGHT",
            TrackerConnected: true,
            TrackerTracked: true,
            MetaLinkReadiness.Ready,
            MetaInputsValid: true,
            InternalDriverProfileReadiness.Calibrated,
            PoseAge: TimeSpan.FromMilliseconds(8),
            IsPublishing: true,
            InternalDriverNeutralReason.None,
            "Right ready.");
        var feed = new InternalDriverFeedSnapshot(
            DriverFeedReadiness.Ready,
            new ProtocolSessionId(0x0123456789ABCDEF, 0x0FEDCBA987654321),
            sequence,
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4),
            ReconnectAttempts: 0,
            LastError: null);
        return new InternalDriverSessionSnapshot(
            InternalDriverSessionState.Active,
            readiness,
            left,
            right,
            feed,
            RestartRequired: false,
            "Active and healthy.",
            "No action required.")
        {
            Timing = new InternalDriverTimingSnapshot(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(2),
                observedTrackerCount: 2),
        };
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp(
            (int)Math.Ceiling(percentile * sorted.Length) - 1,
            0,
            sorted.Length - 1)];

    private static long Percentile(long[] sorted, double percentile) =>
        sorted[Math.Clamp(
            (int)Math.Ceiling(percentile * sorted.Length) - 1,
            0,
            sorted.Length - 1)];

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record WindowMeasurement(
        MainWindow Window,
        InternalDriverViewModel ViewModel,
        double ElapsedMilliseconds,
        long AllocatedBytes,
        int ControlCount,
        int PlotCount);

    private sealed class NotificationCounts
    {
        public int ViewModel { get; private set; }

        public int Left { get; private set; }

        public int Right { get; private set; }

        public int ReadinessRows { get; private set; }

        public int Diagnostics { get; private set; }

        public int TotalPresentation => ViewModel + Left + Right + ReadinessRows;

        public void OnViewModelChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
            ViewModel++;

        public void OnLeftChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
            Left++;

        public void OnRightChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
            Right++;

        public void OnReadinessRowChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
            ReadinessRows++;

        public void OnDiagnosticsChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
            Diagnostics++;

        public void Reset()
        {
            ViewModel = 0;
            Left = 0;
            Right = 0;
            ReadinessRows = 0;
            Diagnostics = 0;
        }
    }

    private sealed class NeverSessionFactory : IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) =>
            throw new InvalidOperationException("No session should be created.");
    }

    private sealed class SingleSessionFactory(IInternalDriverSession session)
        : IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) => session;
    }

    private sealed class ControlledSession(InternalDriverSessionSnapshot initial)
        : IInternalDriverSession
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public InternalDriverSessionSnapshot CurrentSnapshot { get; private set; } = initial;

        public Task Started => _started.Task;

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _stopped.Task.ConfigureAwait(false);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            CurrentSnapshot = CurrentSnapshot with
            {
                State = InternalDriverSessionState.Stopped,
            };
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            _stopped.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(InternalDriverSessionSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public void AllowStop() => _stopped.TrySetResult();
    }

    private sealed class ManualTimeSource : IGuiTimeSource
    {
        private long _timestamp;

        public long GetTimestamp() => _timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class ManualDelayScheduler : IGuiDelayScheduler
    {
        private readonly List<Scheduled> _scheduled = [];

        public int ScheduleCalls { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            ScheduleCalls++;
            var scheduled = new Scheduled(callback);
            _scheduled.Add(scheduled);
            return scheduled;
        }

        public void RunPending()
        {
            foreach (var scheduled in _scheduled.ToArray())
            {
                scheduled.Invoke();
            }
        }

        private sealed class Scheduled(Action callback) : IDisposable
        {
            private bool _disposed;

            public void Dispose() => _disposed = true;

            public void Invoke()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                callback();
            }
        }
    }

    private sealed class SynchronousPrefixControl(TimeSpan delay)
        : IInternalDriverPreSessionControl
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EntryThreadId { get; private set; }

        public InternalDriverPreSessionSnapshot CurrentSnapshot { get; private set; } =
            InternalDriverPreSessionSnapshot.Initial;

        public ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            EntryThreadId = Environment.CurrentManagedThreadId;
            Entered.TrySetResult();
            Thread.Sleep(delay);
            return ValueTask.FromResult(CurrentSnapshot);
        }

        public ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
            string leftTrackerSerial,
            string rightTrackerSerial,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
            bool enabled,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
            InternalDriverManualBindingVerificationEvidence verification,
            InternalDriverManualBindingDecision decision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
