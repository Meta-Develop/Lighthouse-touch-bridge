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
    private readonly IInternalDriverSessionFactory _sessionFactory;
    private readonly Action<Action> _dispatch;
    private readonly Func<IInternalDriverRemover> _removerFactory;
    private readonly object _lifecycleSync = new();
    private readonly Dictionary<string, ReadinessRowViewModel> _rowByKey =
        new(StringComparer.Ordinal);
    private readonly ObservableCollection<ReadinessRowViewModel> _readinessRows = [];
    private long _nextRunGeneration;
    private long _presentedRunGeneration;
    private bool _runActive;
    private bool _removeDriverActive;
    private bool _closing;
    private IInternalDriverSession? _session;
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _runCompletion;
    private Task? _stopOperation;
    private long _activeRunGeneration;
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
    private string _removeDriverStatus =
        "Removes the driver_ltb SteamVR registration; the session must be stopped first.";
    private string _feedState = "Stopped";
    private string _feedSession = "None";
    private string _feedSequence = "None";
    private string _feedHeartbeatAge = "None";
    private string _feedSendAge = "None";
    private int _feedReconnectAttempts;
    private string _feedError = "None";

    public InternalDriverViewModel(
        IInternalDriverSessionFactory sessionFactory,
        Action<Action> dispatch,
        Func<IInternalDriverRemover>? removerFactory = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _removerFactory = removerFactory ?? (static () => InternalDriverRemoval.Create());
        ReadinessRows = new ReadOnlyObservableCollection<ReadinessRowViewModel>(_readinessRows);
        LeftHand = new InternalDriverHandViewModel("Left hand");
        RightHand = new InternalDriverHandViewModel("Right hand");
        ActionCommand = new RelayCommand(
            () => _ = ToggleAsync(),
            () => CanToggle);
        RemoveDriverCommand = new RelayCommand(
            () => _ = RemoveDriverAsync(),
            () => CanRemoveDriver);

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

    public string RemoveDriverStatus
    {
        get => _removeDriverStatus;
        private set => SetProperty(ref _removeDriverStatus, value);
    }

    public bool CanRemoveDriver
    {
        get
        {
            lock (_lifecycleSync)
            {
                return !_closing && !_runActive && !_removeDriverActive;
            }
        }
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

    public RelayCommand RemoveDriverCommand { get; }

    /// <summary>
    /// Secondary maintenance action: transactionally removes the driver_ltb
    /// registration through the restart-safe App removal boundary. It never
    /// runs while a session is active and never alters the Start/Stop flow.
    /// </summary>
    public async Task RemoveDriverAsync()
    {
        lock (_lifecycleSync)
        {
            if (_closing || _runActive || _removeDriverActive)
            {
                return;
            }

            _removeDriverActive = true;
        }

        _dispatch(() =>
        {
            RemoveDriverStatus = "Removing the driver_ltb registration...";
            PresentRemovalAvailability();
        });

        string status;
        try
        {
            await using var remover = _removerFactory() ?? throw new InvalidOperationException(
                "The internal-driver remover factory returned null.");
            var result = await remover.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
            status = result.Diagnostic;
        }
        catch (Exception exception)
        {
            status = $"Driver removal failed: {exception.Message}";
        }
        finally
        {
            lock (_lifecycleSync)
            {
                _removeDriverActive = false;
            }
        }

        _dispatch(() =>
        {
            RemoveDriverStatus = status;
            PresentRemovalAvailability();
        });
    }

    private void PresentRemovalAvailability()
    {
        OnPropertyChanged(nameof(CanRemoveDriver));
        RemoveDriverCommand.RaiseCanExecuteChanged();
    }

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
                if (_closing || _runActive || _removeDriverActive)
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
                _stopOperation = null;
                _activeRunGeneration = generation;
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
            if (!PresentGeneration(generation, session.CurrentSnapshot))
            {
                return;
            }

            LastError = "None";
            IsRunning = true;
            ActionButtonText = "Stop";
            ActionCommand.RaiseCanExecuteChanged();
            PresentRemovalAvailability();
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
                    _activeRunGeneration = 0;
                    _runActive = false;
                }
            }

            var finalSnapshot = session.CurrentSnapshot;
            _dispatch(() =>
            {
                if (!PresentGeneration(generation, finalSnapshot))
                {
                    return;
                }

                var finalError = disposeError ?? runError;
                if (finalError is not null)
                {
                    LastError = finalError;
                    OverallStatus = "Action required";
                }

                IsRunning = false;
                ActionButtonText = "Start";
                ActionCommand.RaiseCanExecuteChanged();
                PresentRemovalAvailability();
            });
            completion.TrySetResult();
        }
    }

    public Task StopAsync()
    {
        IInternalDriverSession? session;
        CancellationTokenSource? cancellation;
        Task? completion;
        long generation;
        lock (_lifecycleSync)
        {
            if (_stopOperation is not null)
            {
                return _stopOperation;
            }

            session = _session;
            cancellation = _runCancellation;
            completion = _runCompletion?.Task;
            generation = _activeRunGeneration;
            if (session is null || completion is null)
            {
                return Task.CompletedTask;
            }

            _stopOperation = StopRunAsync(session, cancellation, completion, generation);
            return _stopOperation;
        }
    }

    private async Task StopRunAsync(
        IInternalDriverSession session,
        CancellationTokenSource? cancellation,
        Task completion,
        long generation)
    {
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
            DispatchError(stopError, generation);
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
            PresentRemovalAvailability();
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
        _dispatch(() => _ = PresentGeneration(generation, snapshot));
    }

    private bool PresentGeneration(long generation, InternalDriverSessionSnapshot snapshot)
    {
        if (generation < _presentedRunGeneration)
        {
            return false;
        }

        _presentedRunGeneration = generation;
        ApplySnapshot(snapshot);
        return true;
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

        LeftHand.Update(snapshot.Left);
        RightHand.Update(snapshot.Right);
        UpdateRows(snapshot);
        UpdateFeed(snapshot);
    }

    private void UpdateRows(InternalDriverSessionSnapshot snapshot)
    {
        var readiness = snapshot.Readiness;
        var driver = snapshot.Driver;
        var registrationReady = readiness.DriverRegistered && driver is not null &&
            !snapshot.RestartRequired;
        var loadedDriverReady = readiness.DriverLoaded && driver?.ExactLoadedBuildReady == true &&
            !snapshot.RestartRequired;
        var hmdReady = snapshot.LighthouseHmd is not null && !snapshot.RestartRequired;
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
            registrationReady,
            snapshot.RestartRequired
                ? "Registration changed; restart SteamVR before this gate can be ready."
                : driver is { } staged
                    ? $"Staged first-party driver build: {staged.StagedBuildIdentity}."
                    : "No staged first-party driver evidence is available.",
            snapshot.RestartRequired ? "Restart required" : null);
        Row("loaded-driver").Update(
            loadedDriverReady,
            DriverDetail(driver),
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
            hmdReady,
            HmdDetail(snapshot.LighthouseHmd),
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
            $"Left: {ProfileDetail(snapshot.Left)} Right: {ProfileDetail(snapshot.Right)}");
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

    private void DispatchError(string message, long? generation = null) =>
        _dispatch(() =>
        {
            if (generation is { } expected && expected < _presentedRunGeneration)
            {
                return;
            }

            LastError = message;
            OverallStatus = "Action required";
        });

    private static string TrackerDetail(InternalDriverHandSnapshot hand) =>
        $"Serial: {hand.TrackerSerial ?? "not assigned"}; connected: {Flag(hand.TrackerConnected)}; " +
        $"tracked: {Flag(hand.TrackerTracked)}; pose age: {FormatAge(hand.PoseAge)}.";

    private static string DriverDetail(InternalDriverDriverEvidence? driver)
    {
        if (driver is null)
        {
            return "No staged or loaded first-party driver evidence is available.";
        }

        if (driver.LeftController is not { } left ||
            driver.RightController is not { } right)
        {
            return $"Staged build: {driver.StagedBuildIdentity}; exact loaded left/right controller evidence is unavailable.";
        }

        return $"Staged build: {driver.StagedBuildIdentity}; " +
            $"left serial: {left.SerialNumber}, runtime build: {left.RuntimeBuildIdentity}; " +
            $"right serial: {right.SerialNumber}, runtime build: {right.RuntimeBuildIdentity}.";
    }

    private static string HmdDetail(InternalDriverLighthouseHmdEvidence? hmd)
    {
        if (hmd is null)
        {
            return "No validated Lighthouse HMD evidence is available.";
        }

        return $"Stable ID: {hmd.StableDeviceId}; device path: {hmd.DevicePath}; " +
            $"driver: {hmd.DriverId}; tracking system: {Optional(hmd.TrackingSystemName)}; " +
            $"manufacturer: {Optional(hmd.ManufacturerName)}; model: {Optional(hmd.ModelNumber)}.";
    }

    private static string ProfileDetail(InternalDriverHandSnapshot hand) =>
        hand.Calibration is { } calibration
            ? $"{hand.ProfileReadiness}, {FormatCalibrationMode(calibration.SelectedMode)}, " +
              $"created {FormatCreated(calibration.CreatedUtc)}."
            : $"{hand.ProfileReadiness}, no retained calibration evidence.";

    private static string Optional(string? value) => value ?? "unavailable";

    private static string FormatCalibrationMode(InternalDriverCalibrationMode mode) => mode switch
    {
        InternalDriverCalibrationMode.RotationOnly => "Rotation only",
        InternalDriverCalibrationMode.FullSixDof => "Full 6DoF",
        _ => mode.ToString(),
    };

    private static string FormatCreated(DateTimeOffset createdUtc) =>
        createdUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value) =>
        (value * 100d).ToString("F1", CultureInfo.InvariantCulture) + "%";

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

    public sealed class InternalDriverHandViewModel : ObservableObject
    {
        private string _trackerSerial = "Not assigned";
        private string _trackerStatus = "Disconnected";
        private string _poseStatus = "Unavailable";
        private string _poseAge = "None";
        private string _inputStatus = "Unavailable";
        private string _profileStatus = "Missing";
        private string _publishingStatus = "Neutral";
        private string _neutralReason = "Session stopped";
        private string _diagnostic = "No active hand session.";
        private string _calibrationMode = "Unavailable";
        private string _calibrationReason = "Unavailable";
        private string _calibrationLag = "Unavailable";
        private string _calibrationQuality = "Unavailable";
        private string _calibrationCreated = "Unavailable";
        private string _captureSamples = "Unavailable";
        private string _captureValidity = "Unavailable";
        private string _captureMotion = "Unavailable";
        private double _rotationProgress;
        private string _rotationProgressStatus = "Unavailable";
        private double _positionProgress;
        private string _positionProgressStatus = "Unavailable";

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

        public string CalibrationMode
        {
            get => _calibrationMode;
            private set => SetProperty(ref _calibrationMode, value);
        }

        public string CalibrationReason
        {
            get => _calibrationReason;
            private set => SetProperty(ref _calibrationReason, value);
        }

        public string CalibrationLag
        {
            get => _calibrationLag;
            private set => SetProperty(ref _calibrationLag, value);
        }

        public string CalibrationQuality
        {
            get => _calibrationQuality;
            private set => SetProperty(ref _calibrationQuality, value);
        }

        public string CalibrationCreated
        {
            get => _calibrationCreated;
            private set => SetProperty(ref _calibrationCreated, value);
        }

        public string CaptureSamples
        {
            get => _captureSamples;
            private set => SetProperty(ref _captureSamples, value);
        }

        public string CaptureValidity
        {
            get => _captureValidity;
            private set => SetProperty(ref _captureValidity, value);
        }

        public string CaptureMotion
        {
            get => _captureMotion;
            private set => SetProperty(ref _captureMotion, value);
        }

        public double RotationProgress
        {
            get => _rotationProgress;
            private set => SetProperty(ref _rotationProgress, value);
        }

        public string RotationProgressStatus
        {
            get => _rotationProgressStatus;
            private set => SetProperty(ref _rotationProgressStatus, value);
        }

        public double PositionProgress
        {
            get => _positionProgress;
            private set => SetProperty(ref _positionProgress, value);
        }

        public string PositionProgressStatus
        {
            get => _positionProgressStatus;
            private set => SetProperty(ref _positionProgressStatus, value);
        }

        internal void Update(InternalDriverHandSnapshot snapshot)
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
            UpdateCalibration(snapshot.Calibration);
            UpdateCapture(snapshot.Capture);
        }

        private void UpdateCalibration(InternalDriverCalibrationEvidence? calibration)
        {
            if (calibration is null)
            {
                CalibrationMode = "Unavailable";
                CalibrationReason = "Unavailable";
                CalibrationLag = "Unavailable";
                CalibrationQuality = "Unavailable";
                CalibrationCreated = "Unavailable";
                return;
            }

            var quality = calibration.Quality;
            var positionRms = quality.PositionRmsMillimeters is { } position
                ? position.ToString("F2", CultureInfo.InvariantCulture) + " mm"
                : "unavailable";
            var translationCondition = quality.TranslationConditionNumber is { } condition
                ? condition.ToString("F2", CultureInfo.InvariantCulture)
                : "unavailable";
            CalibrationMode = FormatCalibrationMode(calibration.SelectedMode);
            CalibrationReason = calibration.SelectionReason;
            CalibrationLag = calibration.EstimatedLagMilliseconds.ToString(
                "F1",
                CultureInfo.InvariantCulture) + " ms";
            CalibrationQuality = string.Create(
                CultureInfo.InvariantCulture,
                $"rotation RMS {quality.RotationRmsDegrees:F2} deg; " +
                $"position RMS {positionRms}; translation condition {translationCondition}; " +
                $"inliers {FormatPercent(quality.InlierRatio)}");
            CalibrationCreated = FormatCreated(calibration.CreatedUtc);
        }

        private void UpdateCapture(InternalDriverCaptureEvidence? capture)
        {
            if (capture is null)
            {
                CaptureSamples = "Unavailable";
                CaptureValidity = "Unavailable";
                CaptureMotion = "Unavailable";
                RotationProgress = 0d;
                RotationProgressStatus = "Unavailable";
                PositionProgress = 0d;
                PositionProgressStatus = "Unavailable";
                return;
            }

            CaptureSamples = capture.SampleCount.ToString(CultureInfo.InvariantCulture);
            CaptureValidity = $"tracking {FormatPercent(capture.TrackingValidityFraction)}; " +
                $"orientation {FormatPercent(capture.OrientationValidityFraction)}; " +
                $"position {FormatPercent(capture.PositionValidityFraction)}";
            CaptureMotion = string.Create(
                CultureInfo.InvariantCulture,
                $"axis coverage {FormatPercent(capture.MotionAxisCoverage)}; " +
                $"total rotation {capture.TotalRotationDegrees:F1} deg");
            RotationProgress = capture.RotationProgress;
            RotationProgressStatus = FormatCaptureProgress(
                capture.RotationProgress,
                capture.RotationReady);
            PositionProgress = capture.PositionProgress;
            PositionProgressStatus = FormatCaptureProgress(
                capture.PositionProgress,
                capture.PositionReady);
        }

        private static string FormatCaptureProgress(double progress, bool ready) =>
            $"{FormatPercent(progress)} - {(ready ? "ready" : "collecting")}";
    }
}
