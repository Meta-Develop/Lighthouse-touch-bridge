using System.Collections.ObjectModel;
using System.Globalization;
using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Presentation-only first-party desktop flow. It consumes immutable App
/// snapshots and never polls or sequences runtime modules itself.
/// </summary>
public sealed class InternalDriverViewModel : ObservableObject, IAsyncDisposable
{
    private const string UnavailableCalibrationMode =
        "Not exposed by the current Ltb.App snapshot.";
    private const string UnavailableCalibrationQuality =
        "Not exposed by the current Ltb.App snapshot.";

    private readonly IInternalDriverSessionFactory _sessionFactory;
    private readonly Action<Action> _dispatch;
    private readonly object _lifecycleSync = new();
    private readonly Dictionary<string, ReadinessRowViewModel> _rowByKey =
        new(StringComparer.Ordinal);
    private readonly ObservableCollection<ReadinessRowViewModel> _readinessRows = [];
    private long _nextRunGeneration;
    private long _presentedRunGeneration;
    private bool _runActive;
    private bool _closing;
    private IInternalDriverSession? _session;
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _runCompletion;
    private InternalDriverSessionState _currentPhase = InternalDriverSessionState.Stopped;
    private string _phaseText = "Stopped";
    private string _diagnostic = "Internal-driver session has not started.";
    private string _remediation = "Press Start to run typed dependency checks.";
    private string _overallStatus = "Stopped";
    private bool _isReady;
    private bool _restartRequired;
    private bool _isRunning;
    private string _actionButtonText = "Start";
    private string _lastError = "None";
    private string _feedState = "Stopped";
    private string _feedSession = "None";
    private string _feedSequence = "None";
    private string _feedHeartbeatAge = "None";
    private string _feedSendAge = "None";
    private int _feedReconnectAttempts;
    private string _feedError = "None";

    public InternalDriverViewModel(
        IInternalDriverSessionFactory sessionFactory,
        Action<Action> dispatch)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        ReadinessRows = new ReadOnlyObservableCollection<ReadinessRowViewModel>(_readinessRows);
        LeftHand = new InternalDriverHandViewModel("Left hand");
        RightHand = new InternalDriverHandViewModel("Right hand");
        ActionCommand = new RelayCommand(
            () => _ = ToggleAsync(),
            () => CanToggle);

        var rows = new[]
        {
            NewRow("platform", "Windows x64"),
            NewRow("steamvr", "SteamVR"),
            NewRow("driver-registration", "Driver registration"),
            NewRow("loaded-driver", "Loaded controllers / build"),
            NewRow("meta-link", "Meta Link"),
            NewRow("left-input", "Left input"),
            NewRow("right-input", "Right input"),
            NewRow("lighthouse-hmd", "Lighthouse HMD"),
            NewRow("left-tracker", "Tracker 1 / left"),
            NewRow("right-tracker", "Tracker 2 / right"),
            NewRow("profiles", "Profiles / calibration"),
            NewRow("feed", "Driver feed"),
        };
        _dispatch(() =>
        {
            foreach (var row in rows)
            {
                _readinessRows.Add(row);
            }
        });
    }

    public ReadOnlyObservableCollection<ReadinessRowViewModel> ReadinessRows { get; }

    public InternalDriverHandViewModel LeftHand { get; }

    public InternalDriverHandViewModel RightHand { get; }

    public InternalDriverSessionState CurrentPhase
    {
        get => _currentPhase;
        private set => SetProperty(ref _currentPhase, value);
    }

    public string PhaseText
    {
        get => _phaseText;
        private set => SetProperty(ref _phaseText, value);
    }

    public string Diagnostic
    {
        get => _diagnostic;
        private set => SetProperty(ref _diagnostic, value);
    }

    public string Remediation
    {
        get => _remediation;
        private set => SetProperty(ref _remediation, value);
    }

    public string OverallStatus
    {
        get => _overallStatus;
        private set => SetProperty(ref _overallStatus, value);
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public bool RestartRequired
    {
        get => _restartRequired;
        private set => SetProperty(ref _restartRequired, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool CanToggle
    {
        get
        {
            lock (_lifecycleSync)
            {
                return !_closing;
            }
        }
    }

    public string ActionButtonText
    {
        get => _actionButtonText;
        private set => SetProperty(ref _actionButtonText, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string FeedState
    {
        get => _feedState;
        private set => SetProperty(ref _feedState, value);
    }

    public string FeedSession
    {
        get => _feedSession;
        private set => SetProperty(ref _feedSession, value);
    }

    public string FeedSequence
    {
        get => _feedSequence;
        private set => SetProperty(ref _feedSequence, value);
    }

    public string FeedHeartbeatAge
    {
        get => _feedHeartbeatAge;
        private set => SetProperty(ref _feedHeartbeatAge, value);
    }

    public string FeedSendAge
    {
        get => _feedSendAge;
        private set => SetProperty(ref _feedSendAge, value);
    }

    public int FeedReconnectAttempts
    {
        get => _feedReconnectAttempts;
        private set => SetProperty(ref _feedReconnectAttempts, value);
    }

    public string FeedError
    {
        get => _feedError;
        private set => SetProperty(ref _feedError, value);
    }

    public RelayCommand ActionCommand { get; }

    public Task ToggleAsync()
    {
        lock (_lifecycleSync)
        {
            return _runActive ? StopAsync() : StartAsync();
        }
    }

    public async Task StartAsync()
    {
        IInternalDriverSession session;
        CancellationTokenSource cancellation;
        TaskCompletionSource completion;
        long generation;
        try
        {
            lock (_lifecycleSync)
            {
                if (_closing || _runActive)
                {
                    return;
                }

                session = _sessionFactory.Create() ??
                    throw new InvalidOperationException("The session factory returned null.");
                cancellation = new CancellationTokenSource();
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                generation = ++_nextRunGeneration;
                _session = session;
                _runCancellation = cancellation;
                _runCompletion = completion;
                _runActive = true;
            }
        }
        catch (Exception exception)
        {
            DispatchError($"Unable to create the internal-driver session: {exception.Message}");
            return;
        }

        EventHandler<InternalDriverSessionSnapshot> snapshotHandler =
            (_, snapshot) => DispatchSnapshot(generation, snapshot);
        session.SnapshotChanged += snapshotHandler;
        _dispatch(() =>
        {
            PresentGeneration(generation, session.CurrentSnapshot);
            LastError = "None";
            IsRunning = true;
            ActionButtonText = "Stop";
            ActionCommand.RaiseCanExecuteChanged();
        });

        string? runError = null;
        try
        {
            await session.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Stop owns cancellation and waits below for App fail-safe shutdown.
        }
        catch (Exception exception)
        {
            runError = $"Internal-driver session failed: {exception.Message}";
        }
        finally
        {
            session.SnapshotChanged -= snapshotHandler;
            string? disposeError = null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposeError = $"Internal-driver disposal failed: {exception.Message}";
            }

            cancellation.Dispose();
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    _runCancellation = null;
                    _runCompletion = null;
                    _runActive = false;
                }
            }

            var finalSnapshot = session.CurrentSnapshot;
            _dispatch(() =>
            {
                PresentGeneration(generation, finalSnapshot);
                var finalError = disposeError ?? runError;
                if (finalError is not null)
                {
                    LastError = finalError;
                    OverallStatus = "Action required";
                }

                IsRunning = false;
                ActionButtonText = "Start";
                ActionCommand.RaiseCanExecuteChanged();
            });
            completion.TrySetResult();
        }
    }

    public async Task StopAsync()
    {
        IInternalDriverSession? session;
        CancellationTokenSource? cancellation;
        Task? completion;
        lock (_lifecycleSync)
        {
            session = _session;
            cancellation = _runCancellation;
            completion = _runCompletion?.Task;
        }

        if (session is null || completion is null)
        {
            return;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run teardown already observed cancellation.
        }

        string? stopError = null;
        try
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopError = $"Internal-driver fail-safe stop failed: {exception.Message}";
        }

        await completion.ConfigureAwait(false);
        if (stopError is not null)
        {
            DispatchError(stopError);
        }
    }

    public async Task CloseAsync()
    {
        lock (_lifecycleSync)
        {
            _closing = true;
        }

        _dispatch(() =>
        {
            OnPropertyChanged(nameof(CanToggle));
            ActionCommand.RaiseCanExecuteChanged();
        });
        await StopAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private ReadinessRowViewModel NewRow(string key, string title)
    {
        var row = new ReadinessRowViewModel(key, title);
        _rowByKey.Add(key, row);
        return row;
    }

    private void DispatchSnapshot(long generation, InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _dispatch(() => PresentGeneration(generation, snapshot));
    }

    private void PresentGeneration(long generation, InternalDriverSessionSnapshot snapshot)
    {
        if (generation < _presentedRunGeneration)
        {
            return;
        }

        _presentedRunGeneration = generation;
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(InternalDriverSessionSnapshot snapshot)
    {
        CurrentPhase = snapshot.State;
        PhaseText = SplitPascalCase(snapshot.State.ToString());
        Diagnostic = snapshot.Diagnostic;
        Remediation = snapshot.Remediation;
        RestartRequired = snapshot.RestartRequired;
        IsReady = snapshot.Readiness.CanPublish && !snapshot.RestartRequired;
        OverallStatus = snapshot.State switch
        {
            InternalDriverSessionState.Stopped => "Stopped",
            _ when snapshot.RestartRequired => "Restart required",
            _ when IsReady => "Ready",
            InternalDriverSessionState.Faulted => "Action required",
            _ => "Waiting",
        };

        LeftHand.Update(snapshot.Left, snapshot.State);
        RightHand.Update(snapshot.Right, snapshot.State);
        UpdateRows(snapshot);
        UpdateFeed(snapshot);
    }

    private void UpdateRows(InternalDriverSessionSnapshot snapshot)
    {
        var readiness = snapshot.Readiness;
        Row("platform").Update(
            readiness.PlatformSupported,
            readiness.PlatformSupported
                ? "The typed App platform gate reports Windows x64 support."
                : "Run the win-x64 desktop build on the SteamVR host.");
        Row("steamvr").Update(
            readiness.SteamVrRunning,
            readiness.SteamVrRunning
                ? "The typed App snapshot reports SteamVR running."
                : "Start SteamVR and wait for its runtime to become available.");
        Row("driver-registration").Update(
            readiness.DriverRegistered && !snapshot.RestartRequired,
            snapshot.RestartRequired
                ? "Registration changed; restart SteamVR before this gate can be ready."
                : "Registration readiness is supplied by Ltb.App.",
            snapshot.RestartRequired ? "Restart required" : null);
        Row("loaded-driver").Update(
            readiness.DriverLoaded && !snapshot.RestartRequired,
            readiness.DriverLoaded
                ? "Ltb.App reports its loaded controller topology/build gate passed; exact identity is not separately exposed."
                : "Wait for Ltb.App to verify the first-party loaded-driver topology.",
            snapshot.RestartRequired ? "Restart required" : null);
        Row("meta-link").Update(
            readiness.MetaBothHandsReady,
            readiness.MetaBothHandsReady
                ? "The typed App snapshot reports both Meta Link hands ready."
                : "Connect Quest Link or Air Link and keep both controllers awake.");
        Row("left-input").Update(
            snapshot.Left.MetaInputsValid,
            $"Meta state: {snapshot.Left.MetaReadiness}; inputs valid: {Flag(snapshot.Left.MetaInputsValid)}.");
        Row("right-input").Update(
            snapshot.Right.MetaInputsValid,
            $"Meta state: {snapshot.Right.MetaReadiness}; inputs valid: {Flag(snapshot.Right.MetaInputsValid)}.");
        Row("lighthouse-hmd").Update(
            readiness.DriverLoaded && !snapshot.RestartRequired,
            "HMD identity is not separately exposed by the App snapshot; this row follows the typed loaded-readiness gate that includes topology validation.",
            snapshot.RestartRequired ? "Restart required" : null);
        Row("left-tracker").Update(
            readiness.TwoDistinctTrackersReady &&
                snapshot.Left.TrackerConnected &&
                snapshot.Left.TrackerTracked,
            $"{TrackerDetail(snapshot.Left)} Distinct-pair gate: {Flag(readiness.TwoDistinctTrackersReady)}.");
        Row("right-tracker").Update(
            readiness.TwoDistinctTrackersReady &&
                snapshot.Right.TrackerConnected &&
                snapshot.Right.TrackerTracked,
            $"{TrackerDetail(snapshot.Right)} Distinct-pair gate: {Flag(readiness.TwoDistinctTrackersReady)}.");
        Row("profiles").Update(
            readiness.ProfilesReady,
            $"Left: {snapshot.Left.ProfileReadiness}; right: {snapshot.Right.ProfileReadiness}. " +
            "Selected calibration mode and quality metrics are not exposed by the current App snapshot.");
        Row("feed").Update(
            readiness.FeedReady,
            $"Feed state: {snapshot.Feed.Readiness}; reconnect attempts: {snapshot.Feed.ReconnectAttempts}.");
    }

    private void UpdateFeed(InternalDriverSessionSnapshot snapshot)
    {
        var feed = snapshot.Feed;
        FeedState = feed.Readiness.ToString();
        FeedSession = feed.SessionId is { } sessionId
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{sessionId.Word0:X16}{sessionId.Word1:X16}")
            : "None";
        FeedSequence = feed.LastSuccessfulSequence?.ToString(CultureInfo.InvariantCulture) ?? "None";
        FeedHeartbeatAge = FormatAge(feed.LastSuccessfulHeartbeatAge);
        FeedSendAge = FormatAge(feed.LastSuccessfulSendAge);
        FeedReconnectAttempts = feed.ReconnectAttempts;
        FeedError = string.IsNullOrWhiteSpace(feed.LastError) ? "None" : feed.LastError;
    }

    private ReadinessRowViewModel Row(string key) => _rowByKey[key];

    private void DispatchError(string message) =>
        _dispatch(() =>
        {
            LastError = message;
            OverallStatus = "Action required";
        });

    private static string TrackerDetail(InternalDriverHandSnapshot hand) =>
        $"Serial: {hand.TrackerSerial ?? "not assigned"}; connected: {Flag(hand.TrackerConnected)}; " +
        $"tracked: {Flag(hand.TrackerTracked)}; pose age: {FormatAge(hand.PoseAge)}.";

    private static string FormatAge(TimeSpan? age) => age is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:F1} ms")
        : "None";

    private static string Flag(bool value) => value ? "yes" : "no";

    private static string SplitPascalCase(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var characters = new List<char>(value.Length + 4) { value[0] };
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsUpper(value[index - 1]))
            {
                characters.Add(' ');
            }

            characters.Add(value[index]);
        }

        return new string([.. characters]);
    }

    internal static double GlobalCalibrationPhaseEstimateFor(
        InternalDriverSessionState state,
        InternalDriverProfileReadiness profileReadiness)
    {
        if (profileReadiness is InternalDriverProfileReadiness.Reused or
            InternalDriverProfileReadiness.Calibrated)
        {
            return 1d;
        }

        return state switch
        {
            InternalDriverSessionState.Recording => 0.15d,
            InternalDriverSessionState.Association => 0.30d,
            InternalDriverSessionState.TimeAlignment => 0.45d,
            InternalDriverSessionState.RotationSolve => 0.60d,
            InternalDriverSessionState.TranslationAttempt => 0.72d,
            InternalDriverSessionState.Validation => 0.85d,
            InternalDriverSessionState.SaveProfile => 0.95d,
            _ => 0d,
        };
    }

    public sealed class InternalDriverHandViewModel : ObservableObject
    {
        private readonly string _calibrationMode = UnavailableCalibrationMode;
        private readonly string _calibrationQuality = UnavailableCalibrationQuality;
        private string _trackerSerial = "Not assigned";
        private string _trackerStatus = "Disconnected";
        private string _poseStatus = "Unavailable";
        private string _poseAge = "None";
        private string _inputStatus = "Unavailable";
        private string _profileStatus = "Missing";
        private string _publishingStatus = "Neutral";
        private string _neutralReason = "Session stopped";
        private string _diagnostic = "No active hand session.";
        private double _globalCalibrationPhaseEstimate;

        internal InternalDriverHandViewModel(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public string TrackerSerial
        {
            get => _trackerSerial;
            private set => SetProperty(ref _trackerSerial, value);
        }

        public string TrackerStatus
        {
            get => _trackerStatus;
            private set => SetProperty(ref _trackerStatus, value);
        }

        public string PoseStatus
        {
            get => _poseStatus;
            private set => SetProperty(ref _poseStatus, value);
        }

        public string PoseAge
        {
            get => _poseAge;
            private set => SetProperty(ref _poseAge, value);
        }

        public string InputStatus
        {
            get => _inputStatus;
            private set => SetProperty(ref _inputStatus, value);
        }

        public string ProfileStatus
        {
            get => _profileStatus;
            private set => SetProperty(ref _profileStatus, value);
        }

        public string PublishingStatus
        {
            get => _publishingStatus;
            private set => SetProperty(ref _publishingStatus, value);
        }

        public string NeutralReason
        {
            get => _neutralReason;
            private set => SetProperty(ref _neutralReason, value);
        }

        public string Diagnostic
        {
            get => _diagnostic;
            private set => SetProperty(ref _diagnostic, value);
        }

        /// <summary>
        /// A session-wide phase estimate rendered in both hand cards. This is
        /// not measured per-hand progress or motion-coverage evidence.
        /// </summary>
        public double GlobalCalibrationPhaseEstimate
        {
            get => _globalCalibrationPhaseEstimate;
            private set => SetProperty(ref _globalCalibrationPhaseEstimate, value);
        }

        public string CalibrationMode => _calibrationMode;

        public string CalibrationQuality => _calibrationQuality;

        internal void Update(
            InternalDriverHandSnapshot snapshot,
            InternalDriverSessionState state)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            TrackerSerial = snapshot.TrackerSerial ?? "Not assigned";
            TrackerStatus = snapshot.TrackerConnected
                ? snapshot.TrackerTracked ? "Tracked" : "Connected / not tracked"
                : "Disconnected";
            PoseStatus = snapshot.TrackerTracked ? "Tracked" : "Unavailable";
            PoseAge = FormatAge(snapshot.PoseAge);
            InputStatus = snapshot.MetaInputsValid
                ? $"Ready ({snapshot.MetaReadiness})"
                : $"Unavailable ({snapshot.MetaReadiness})";
            ProfileStatus = snapshot.ProfileReadiness.ToString();
            PublishingStatus = snapshot.IsPublishing ? "Publishing" : "Neutral";
            NeutralReason = SplitPascalCase(snapshot.NeutralReason.ToString());
            Diagnostic = snapshot.Diagnostic;
            GlobalCalibrationPhaseEstimate =
                GlobalCalibrationPhaseEstimateFor(state, snapshot.ProfileReadiness);
        }
    }
}
