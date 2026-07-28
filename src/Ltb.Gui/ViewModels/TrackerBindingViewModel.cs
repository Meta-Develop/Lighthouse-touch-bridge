using System.Collections.ObjectModel;
using System.Globalization;
using Ltb.App;
using Ltb.Configuration;
using Ltb.OpenVr;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Presentation over the App-owned stopped/pre-session binding and lifecycle
/// boundary. It performs no OpenVR, process, settings, or registration work.
/// </summary>
public sealed class TrackerBindingViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IInternalDriverPreSessionControl _control;
    private readonly Action<Action> _dispatch;
    private readonly ObservableCollection<InternalDriverPairedTrackerOption> _trackers = [];
    private readonly object _operationSync = new();
    private readonly SemaphoreSlim _controlSerialization = new(1, 1);
    private bool _isBusy;
    private int _operationCount;
    private int _refreshOperationCount;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private InternalDriverPairedTrackerOption? _selectedLeftTracker;
    private InternalDriverPairedTrackerOption? _selectedRightTracker;
    private bool _unregisterOnExit = true;
    private string _statusText =
        "Paired Lighthouse trackers have not been inspected.";
    private string _remediationText = "Refresh while the session is stopped.";
    private string _registrationStateText = "Not inspected";
    private string _steamVrProcessText = "Not inspected";
    private string _verificationStatusText =
        "Motion-correlation verification has not run for a manual binding.";
    private string _externalRegistrationWarningsText =
        "Recognized external SteamVR registrations have not been inspected.";
    private string _recoveryCandidatesText =
        "SteamVR settings recovery candidates have not been inspected.";
    private string _trackerRoleDriftText =
        "No retained tracker-role receipt has been inspected.";
    private string _trackerPathEvidenceText =
        "No live tracker-path evidence has been loaded.";
    private string _storedLeftQualityText =
        "No exact reusable stored left-hand profile is selected.";
    private string _storedRightQualityText =
        "No exact reusable stored right-hand profile is selected.";
    private string _storedPairQualityText =
        "No exact reusable stored profile pair is selected.";
    private bool _hasExternalRegistrationWarnings;
    private bool _hasRecoveryCandidates;
    private bool _hasTrackerRoleDrift;
    private bool _hasManualBinding;
    private bool _hasCorrectionChoice;
    private bool _restartRequired;
    private InternalDriverManualBindingVerificationEvidence? _verification;
    private bool _disposed;

    public TrackerBindingViewModel(
        IInternalDriverPreSessionControl control,
        Action<Action> dispatch)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        Trackers = new ReadOnlyObservableCollection<InternalDriverPairedTrackerOption>(_trackers);
        RefreshCommand = new RelayCommand(
            () => _ = RefreshAsync(),
            () => CanEdit);
        SaveBindingCommand = new RelayCommand(
            () => _ = SaveBindingAsync(),
            () => CanSaveBinding);
        ClearBindingCommand = new RelayCommand(
            () => _ = ClearBindingAsync(),
            () => CanEdit && HasManualBinding);
        SaveLifecyclePolicyCommand = new RelayCommand(
            () => _ = SaveLifecyclePolicyAsync(),
            () => CanEdit);
        RetainManualBindingCommand = new RelayCommand(
            () => _ = ApplyVerificationDecisionAsync(
                InternalDriverManualBindingDecision.RetainManualBinding),
            () => CanChooseCorrection);
        AcceptCorrectionCommand = new RelayCommand(
            () => _ = ApplyVerificationDecisionAsync(
                InternalDriverManualBindingDecision.AcceptCorrectionCandidate),
            () => CanChooseCorrection);
        Apply(_control.CurrentSnapshot);
    }

    public ReadOnlyObservableCollection<InternalDriverPairedTrackerOption> Trackers { get; }

    public InternalDriverPairedTrackerOption? SelectedLeftTracker
    {
        get => _selectedLeftTracker;
        set
        {
            if (SetProperty(ref _selectedLeftTracker, value))
            {
                NotifyBindingSelectionChanged();
            }
        }
    }

    public InternalDriverPairedTrackerOption? SelectedRightTracker
    {
        get => _selectedRightTracker;
        set
        {
            if (SetProperty(ref _selectedRightTracker, value))
            {
                NotifyBindingSelectionChanged();
            }
        }
    }

    public bool UnregisterOnExit
    {
        get => _unregisterOnExit;
        set => SetProperty(ref _unregisterOnExit, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string RemediationText
    {
        get => _remediationText;
        private set => SetProperty(ref _remediationText, value);
    }

    public string RegistrationStateText
    {
        get => _registrationStateText;
        private set => SetProperty(ref _registrationStateText, value);
    }

    public string SteamVrProcessText
    {
        get => _steamVrProcessText;
        private set => SetProperty(ref _steamVrProcessText, value);
    }

    public string VerificationStatusText
    {
        get => _verificationStatusText;
        private set => SetProperty(ref _verificationStatusText, value);
    }

    public string ExternalRegistrationWarningsText
    {
        get => _externalRegistrationWarningsText;
        private set => SetProperty(ref _externalRegistrationWarningsText, value);
    }

    public string RecoveryCandidatesText
    {
        get => _recoveryCandidatesText;
        private set => SetProperty(ref _recoveryCandidatesText, value);
    }

    public string TrackerRoleDriftText
    {
        get => _trackerRoleDriftText;
        private set => SetProperty(ref _trackerRoleDriftText, value);
    }

    public string TrackerPathEvidenceText
    {
        get => _trackerPathEvidenceText;
        private set => SetProperty(ref _trackerPathEvidenceText, value);
    }

    public string StoredLeftQualityText
    {
        get => _storedLeftQualityText;
        private set => SetProperty(ref _storedLeftQualityText, value);
    }

    public string StoredRightQualityText
    {
        get => _storedRightQualityText;
        private set => SetProperty(ref _storedRightQualityText, value);
    }

    public string StoredPairQualityText
    {
        get => _storedPairQualityText;
        private set => SetProperty(ref _storedPairQualityText, value);
    }

    public bool HasExternalRegistrationWarnings
    {
        get => _hasExternalRegistrationWarnings;
        private set => SetProperty(ref _hasExternalRegistrationWarnings, value);
    }

    public bool HasRecoveryCandidates
    {
        get => _hasRecoveryCandidates;
        private set => SetProperty(ref _hasRecoveryCandidates, value);
    }

    public bool HasTrackerRoleDrift
    {
        get => _hasTrackerRoleDrift;
        private set => SetProperty(ref _hasTrackerRoleDrift, value);
    }

    public bool HasManualBinding
    {
        get => _hasManualBinding;
        private set => SetProperty(ref _hasManualBinding, value);
    }

    public bool RestartRequired
    {
        get => _restartRequired;
        private set => SetProperty(ref _restartRequired, value);
    }

    public bool HasCorrectionChoice
    {
        get => _hasCorrectionChoice;
        private set => SetProperty(ref _hasCorrectionChoice, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandAvailabilityChanged();
            }
        }
    }

    public bool CanEdit
    {
        get
        {
            lock (_operationSync)
            {
                return _operationCount == 0 && !_disposed;
            }
        }
    }

    public bool CanSaveBinding =>
        CanEdit &&
        SelectedLeftTracker is not null &&
        SelectedRightTracker is not null &&
        !string.Equals(
            SelectedLeftTracker.Serial,
            SelectedRightTracker.Serial,
            StringComparison.OrdinalIgnoreCase);

    public bool CanChooseCorrection =>
        CanEdit &&
        HasCorrectionChoice &&
        _verification is { RequiresDecision: true };

    public RelayCommand RefreshCommand { get; }

    public RelayCommand SaveBindingCommand { get; }

    public RelayCommand ClearBindingCommand { get; }

    public RelayCommand SaveLifecyclePolicyCommand { get; }

    public RelayCommand RetainManualBindingCommand { get; }

    public RelayCommand AcceptCorrectionCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public async Task<bool> PrepareStartAsync()
    {
        if (!TryBeginOperation())
        {
            return false;
        }

        try
        {
            var result = await Task.Run(
                    () => RunSerializedControlAsync(
                        _control.PrepareStartAsync,
                        CancellationToken.None),
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() => Apply(result));
            return result.CanStart;
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Start preflight failed: {exception.Message}"));
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync()
    {
        if (!TryBeginControlledStopOperation(out var refreshCancellation))
        {
            return _control.CurrentSnapshot;
        }

        CancelRefresh(refreshCancellation);
        try
        {
            var result = await RunSerializedControlAsync(
                    _control.CompleteControlledStopAsync,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() => Apply(result));
            return result;
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Controlled-stop cleanup failed: {exception.Message}"));
            return _control.CurrentSnapshot;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task RefreshAsync()
    {
        if (!TryBeginRefresh(
                out var generation,
                out var cancellation))
        {
            return;
        }

        try
        {
            var result = await Task.Run(
                    () => RunSerializedControlAsync(
                        _control.RefreshAsync,
                        cancellation.Token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            DispatchRefreshIfCurrent(generation, () => Apply(result));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer refresh or disposal owns cancellation. Stale generations
            // never replace the latest stopped-panel state or diagnostic.
        }
        catch (Exception exception)
        {
            DispatchRefreshIfCurrent(
                generation,
                () => PresentUnexpectedFailure(
                    $"Stopped/pre-session refresh failed: {exception.Message}"));
        }
        finally
        {
            EndRefreshOperation(cancellation);
        }
    }

    public async Task SaveBindingAsync()
    {
        var left = SelectedLeftTracker;
        var right = SelectedRightTracker;
        if (!CanSaveBinding || left is null || right is null || !TryBeginOperation())
        {
            return;
        }

        try
        {
            var result = await RunSerializedControlAsync(
                    cancellationToken => _control.SaveManualBindingAsync(
                        left.Serial,
                        right.Serial,
                        cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() => Apply(result));
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Manual tracker binding was not saved: {exception.Message}"));
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ClearBindingAsync()
    {
        if (!CanEdit || !TryBeginOperation())
        {
            return;
        }

        try
        {
            var result = await RunSerializedControlAsync(
                    _control.ClearManualBindingAsync,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() => Apply(result));
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Manual tracker binding was not cleared: {exception.Message}"));
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task SaveLifecyclePolicyAsync()
    {
        if (!CanEdit || !TryBeginOperation())
        {
            return;
        }

        var requested = UnregisterOnExit;
        try
        {
            var result = await RunSerializedControlAsync(
                    cancellationToken => _control.SetUnregisterOnExitAsync(
                        requested,
                        cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() => Apply(result));
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Unregister-on-exit policy was not saved: {exception.Message}"));
        }
        finally
        {
            EndOperation();
        }
    }

    internal void ApplyVerification(
        InternalDriverManualBindingVerificationEvidence verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        _verification = verification;
        VerificationStatusText = verification.State switch
        {
            InternalDriverManualBindingVerificationState.Agreement =>
                $"Agreement: {verification.Diagnostic}",
            InternalDriverManualBindingVerificationState.CorrelationFailed =>
                $"Unverified, manual pair retained: {verification.Diagnostic}",
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate =>
                $"Mismatch: {verification.Diagnostic} Choose Retain manual binding or " +
                $"Accept correction explicitly.",
            _ => throw new ArgumentOutOfRangeException(nameof(verification)),
        };
        HasCorrectionChoice = verification.RequiresDecision;
        NotifyCommandAvailabilityChanged();
    }

    internal async Task ApplyVerificationDecisionAsync(
        InternalDriverManualBindingDecision decision)
    {
        var verification = _verification;
        if (!CanChooseCorrection || verification is null || !TryBeginOperation())
        {
            return;
        }

        try
        {
            var result = await RunSerializedControlAsync(
                    cancellationToken => _control.ApplyManualBindingDecisionAsync(
                        verification,
                        decision,
                        cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
            _dispatch(() =>
            {
                Apply(result);
                _verification = null;
                HasCorrectionChoice = false;
                VerificationStatusText =
                    result.State == InternalDriverPreSessionState.ManualBindingDecisionStale
                        ? "Pending verification decision cleared: authoritative pre-session " +
                          "settings changed by value or generation. Refresh and rerun motion " +
                          "verification; no newer settings were overwritten."
                        : decision ==
                            InternalDriverManualBindingDecision.AcceptCorrectionCandidate
                            ? "Correction accepted explicitly and saved for the next preflight."
                            : "Authoritative manual binding retained explicitly.";
                NotifyCommandAvailabilityChanged();
            });
        }
        catch (Exception exception)
        {
            _dispatch(() =>
            {
                _verification = null;
                HasCorrectionChoice = false;
                VerificationStatusText =
                    "Pending verification decision cleared because its authority could not " +
                    "be committed. Refresh and rerun motion verification; no decision retry " +
                    "will occur automatically.";
                PresentUnexpectedFailure(
                    $"Manual-binding verification decision was not saved: {exception.Message}");
            });
        }
        finally
        {
            EndOperation();
        }
    }

    private void Apply(InternalDriverPreSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requestedLeft = snapshot.LeftTrackerSerial ?? SelectedLeftTracker?.Serial;
        var requestedRight = snapshot.RightTrackerSerial ?? SelectedRightTracker?.Serial;
        if (!TrackersMatch(snapshot.PairedTrackers))
        {
            _trackers.Clear();
            foreach (var tracker in snapshot.PairedTrackers)
            {
                _trackers.Add(tracker);
            }
        }

        SelectedLeftTracker = FindTracker(requestedLeft);
        SelectedRightTracker = FindTracker(requestedRight);
        UnregisterOnExit = snapshot.UnregisterOnExit;
        HasManualBinding = snapshot.HasManualBinding;
        RestartRequired = snapshot.RestartRequired;
        StatusText = snapshot.Diagnostic;
        RemediationText = snapshot.Remediation;
        RegistrationStateText = snapshot.RegistrationState?.ToString() ?? "Not inspected";
        SteamVrProcessText = snapshot.SteamVrProcesses.IsAnyRunning
            ? $"Running: {snapshot.SteamVrProcesses.RunningProcessList}"
            : "Stopped: vrserver and vrmonitor are not running";
        PresentExternalRegistrationWarnings(snapshot);
        PresentRecoveryCandidates(snapshot);
        PresentTrackerRoleDrift(snapshot);
        PresentTrackerPathEvidence(snapshot);
        PresentStoredQuality(snapshot.StoredProfileQuality);
        ClearPendingVerificationAfterAuthoritativeRefresh();
        NotifyCommandAvailabilityChanged();
    }

    private void ClearPendingVerificationAfterAuthoritativeRefresh()
    {
        if (_verification is null)
        {
            return;
        }

        _verification = null;
        HasCorrectionChoice = false;
        VerificationStatusText =
            "Pending verification decision cleared because authoritative pre-session state " +
            "was refreshed. Rerun motion verification before deciding, even when the same " +
            "manual pair is still selected.";
    }

    private void PresentExternalRegistrationWarnings(
        InternalDriverPreSessionSnapshot snapshot)
    {
        var warnings = snapshot.ExternalRegistrationWarnings;
        HasExternalRegistrationWarnings = warnings.Count != 0;
        ExternalRegistrationWarningsText = warnings.Count == 0
            ? "No recognized unrelated SteamVR registrations were reported."
            : string.Join(
                Environment.NewLine,
                warnings.Select(warning =>
                    $"{warning.DisplayName}: registered root {warning.RegisteredDriverRoot}. " +
                    "Registration is not evidence that this integration is loaded, running, " +
                    $"or publishing. {warning.Guidance} LTB will not modify this registration."));
    }

    private void PresentRecoveryCandidates(InternalDriverPreSessionSnapshot snapshot)
    {
        var candidates = snapshot.RecoveryDiscovery?.Candidates ?? [];
        HasRecoveryCandidates = candidates.Count != 0;
        RecoveryCandidatesText = candidates.Count == 0
            ? "No recognized LTB SteamVR settings backup candidate was found. Discovery " +
              "uses metadata only and never reads or restores backup contents."
            : "Metadata-only recovery candidates (no backup content was read and no automatic " +
              "restore will run) beside settings file " +
              $"{snapshot.RecoveryDiscovery!.SettingsFilePath}:" + Environment.NewLine +
              string.Join(
                  Environment.NewLine,
                  candidates.Select(candidate => string.Create(
                      CultureInfo.InvariantCulture,
                      $"{candidate.BackupFilePath} · sequence {candidate.SequenceNumber} · " +
                      $"{candidate.LengthBytes} bytes · " +
                      $"{candidate.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss 'UTC'}"))) +
              Environment.NewLine +
              "Inspect current settings and the candidate manually before choosing any " +
              "recovery action outside this read-only panel.";
    }

    private void PresentTrackerRoleDrift(InternalDriverPreSessionSnapshot snapshot)
    {
        var drift = snapshot.TrackerRoleDrift;
        HasTrackerRoleDrift = drift?.HasDrift == true;
        TrackerRoleDriftText = drift is null
            ? "No valid retained LTB tracker-role receipt is available for exact-path drift inspection."
            : $"{FormatRoleDrift("Left", drift.LeftTracker)} " +
              $"{FormatRoleDrift("Right", drift.RightTracker)} " +
              "This report is read-only; LTB did not rewrite, restore, or re-neutralize either role.";
    }

    private static string FormatRoleDrift(string hand, TrackerRoleDriftEntry entry) =>
        $"{hand} exact path {entry.RegisteredDevicePath}: {entry.Status}" +
        (entry.ObservedRole is { } role ? $" (observed role {role})." : ".");

    private void PresentTrackerPathEvidence(InternalDriverPreSessionSnapshot snapshot)
    {
        var details = snapshot.TrackerPathObservations.Select(observation =>
        {
            var history = observation.PathChangeHistory.Count == 0
                ? "no prior registered-path history"
                : "prior history: " + string.Join(
                    " | ",
                    observation.PathChangeHistory.Select(entry =>
                        $"{entry.PriorRegisteredDevicePath} last observed " +
                        $"{FormatUtc(entry.PriorLastObservedUtc)}, replaced " +
                        $"{FormatUtc(entry.ReplacementUtc)}"));
            return $"{observation.TrackerSerial}: current exact path " +
                $"{observation.RegisteredDevicePath}, last observed " +
                $"{FormatUtc(observation.LastObservedUtc)}; {history}.";
        });
        TrackerPathEvidenceText =
            $"{snapshot.TrackerPathEvidenceDiagnostic} Pending reconciliation: " +
            $"{(snapshot.TrackerPathReconciliationPending ? "yes" : "no")}. " +
            "One normal live session is required to refresh current observations." +
            (snapshot.TrackerPathObservations.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, details));
    }

    private void PresentStoredQuality(StoredCalibrationProfilePairAssessment? assessment)
    {
        if (assessment is null)
        {
            StoredLeftQualityText =
                "No exact reusable stored left-hand profile is selected; position quality is unavailable.";
            StoredRightQualityText =
                "No exact reusable stored right-hand profile is selected; position quality is unavailable.";
            StoredPairQualityText =
                "No exact reusable stored pair is selected; lever-arm comparison is insufficient evidence, not poor quality.";
            return;
        }

        StoredLeftQualityText = FormatStoredHandQuality("Left", assessment.Left);
        StoredRightQualityText = FormatStoredHandQuality("Right", assessment.Right);
        StoredPairQualityText = assessment.LeverArmGuidance switch
        {
            StoredCalibrationLeverArmGuidance.MaterialMagnitudeDisagreement =>
                "Material lever-arm magnitude difference: " +
                assessment.LeverArmMagnitudeDifferenceMillimeters!.Value.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
                " mm is at or above " +
                StoredCalibrationProfileQualityAssessor
                    .MaterialLeverArmMagnitudeDifferenceMillimeters
                    .ToString("F2", CultureInfo.InvariantCulture) +
                " mm. Inspect the mounts before headset use.",
            StoredCalibrationLeverArmGuidance.WithinOperationalGuidance =>
                "Stored lever-arm magnitude difference " +
                assessment.LeverArmMagnitudeDifferenceMillimeters!.Value.ToString(
                    "F2",
                    CultureInfo.InvariantCulture) +
                " mm is below the " +
                StoredCalibrationProfileQualityAssessor
                    .MaterialLeverArmMagnitudeDifferenceMillimeters
                    .ToString("F2", CultureInfo.InvariantCulture) +
                " mm guidance boundary.",
            StoredCalibrationLeverArmGuidance.InsufficientEvidence =>
                "Stored lever-arm comparison is insufficient evidence because one or both " +
                "profiles are rotation-only; this is not poor quality.",
            _ => throw new ArgumentOutOfRangeException(nameof(assessment)),
        };
    }

    private static string FormatStoredHandQuality(
        string hand,
        StoredCalibrationProfileAssessment assessment) =>
        assessment.PositionGuidance switch
        {
            StoredCalibrationPositionGuidance.RecaptureRecommended =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{hand} stored profile is worth recapturing: position RMS " +
                    $"{assessment.PositionRmsMillimeters:F2} mm is at or above " +
                    $"{StoredCalibrationProfileQualityAssessor.PositionRmsRecaptureGuidanceMillimeters:F2} mm."),
            StoredCalibrationPositionGuidance.WithinOperationalGuidance =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{hand} stored full-6DoF position RMS " +
                    $"{assessment.PositionRmsMillimeters:F2} mm is below the " +
                    $"{StoredCalibrationProfileQualityAssessor.PositionRmsRecaptureGuidanceMillimeters:F2} mm recapture boundary."),
            StoredCalibrationPositionGuidance.InsufficientEvidence =>
                $"{hand} stored {assessment.SelectedMode} profile has insufficient position " +
                "evidence; rotation-only or absent position metrics are not poor quality.",
            _ => throw new ArgumentOutOfRangeException(nameof(assessment)),
        };

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture);

    private bool TrackersMatch(
        IReadOnlyList<InternalDriverPairedTrackerOption> pairedTrackers)
    {
        if (_trackers.Count != pairedTrackers.Count)
        {
            return false;
        }

        for (var index = 0; index < _trackers.Count; index++)
        {
            if (_trackers[index] != pairedTrackers[index])
            {
                return false;
            }
        }

        return true;
    }

    private InternalDriverPairedTrackerOption? FindTracker(string? serial) =>
        serial is null
            ? null
            : _trackers.FirstOrDefault(tracker =>
                string.Equals(
                    tracker.Serial,
                    serial,
                    StringComparison.OrdinalIgnoreCase));

    private bool TryBeginOperation(bool allowWhileBusy = false)
    {
        bool becameBusy;
        lock (_operationSync)
        {
            if (_disposed || !allowWhileBusy && _operationCount != 0)
            {
                return false;
            }

            becameBusy = _operationCount++ == 0;
            if (becameBusy)
            {
                _operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (becameBusy)
        {
            _dispatch(() => IsBusy = true);
        }

        return true;
    }

    private bool TryBeginControlledStopOperation(
        out CancellationTokenSource? refreshCancellation)
    {
        bool becameBusy;
        lock (_operationSync)
        {
            if (_disposed)
            {
                refreshCancellation = null;
                return false;
            }

            becameBusy = _operationCount++ == 0;
            if (becameBusy)
            {
                _operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            refreshCancellation = _refreshCancellation;
        }

        if (becameBusy)
        {
            _dispatch(() => IsBusy = true);
        }

        return true;
    }

    private bool TryBeginRefresh(
        out long generation,
        out CancellationTokenSource cancellation)
    {
        CancellationTokenSource? replaced;
        bool becameBusy;
        lock (_operationSync)
        {
            if (_disposed || _operationCount != _refreshOperationCount)
            {
                generation = 0;
                cancellation = null!;
                return false;
            }

            becameBusy = _operationCount++ == 0;
            _refreshOperationCount++;
            if (becameBusy)
            {
                _operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            generation = ++_refreshGeneration;
            cancellation = new CancellationTokenSource();
            replaced = _refreshCancellation;
            _refreshCancellation = cancellation;
        }

        CancelRefresh(replaced);
        if (becameBusy)
        {
            _dispatch(() => IsBusy = true);
        }

        return true;
    }

    private void EndOperation()
    {
        TaskCompletionSource? operationsDrained = null;
        lock (_operationSync)
        {
            if (_operationCount <= 0)
            {
                throw new InvalidOperationException(
                    "Tracker-binding operation accounting became unbalanced.");
            }

            _operationCount--;
            if (_operationCount == 0)
            {
                operationsDrained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        if (operationsDrained is not null)
        {
            _dispatch(() => IsBusy = false);
            operationsDrained.TrySetResult();
        }
    }

    private void EndRefreshOperation(CancellationTokenSource cancellation)
    {
        lock (_operationSync)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
            }

            if (_refreshOperationCount <= 0)
            {
                throw new InvalidOperationException(
                    "Tracker-binding refresh accounting became unbalanced.");
            }

            _refreshOperationCount--;
        }

        try
        {
            EndOperation();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task<T> RunSerializedControlAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await _controlSerialization
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlSerialization.Release();
        }
    }

    private void DispatchRefreshIfCurrent(long generation, Action action) =>
        _dispatch(() =>
        {
            lock (_operationSync)
            {
                if (_disposed || generation != _refreshGeneration)
                {
                    return;
                }

                action();
            }
        });

    private static void CancelRefresh(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may win the race with a replacement or disposal.
        }
        catch (AggregateException)
        {
            // A control-owned cancellation callback must not unbalance refresh
            // replacement or disposal accounting.
        }
    }

    private void PresentUnexpectedFailure(string message)
    {
        StatusText = message;
        RemediationText =
            "Review the stopped/pre-session diagnostic and try again. No runtime claim is implied.";
    }

    private void NotifyBindingSelectionChanged()
    {
        OnPropertyChanged(nameof(CanSaveBinding));
        SaveBindingCommand.RaiseCanExecuteChanged();
    }

    private void NotifyCommandAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSaveBinding));
        RefreshCommand.RaiseCanExecuteChanged();
        SaveBindingCommand.RaiseCanExecuteChanged();
        ClearBindingCommand.RaiseCanExecuteChanged();
        SaveLifecyclePolicyCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanChooseCorrection));
        RetainManualBindingCommand.RaiseCanExecuteChanged();
        AcceptCorrectionCommand.RaiseCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        bool notifyAvailability = false;
        CancellationTokenSource? refreshCancellation;
        Task disposeTask;
        lock (_operationSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            notifyAvailability = true;
            refreshCancellation = _refreshCancellation;
            var operationsDrained = _operationCount == 0
                ? Task.CompletedTask
                : _operationsDrained!.Task;
            _disposeTask = DisposeControlAfterOperationsAsync(operationsDrained);
            disposeTask = _disposeTask;
        }

        CancelRefresh(refreshCancellation);
        if (notifyAvailability)
        {
            _dispatch(NotifyCommandAvailabilityChanged);
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeControlAfterOperationsAsync(Task operationsDrained)
    {
        await operationsDrained.ConfigureAwait(false);
        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _controlSerialization.Dispose();
        }
    }
}

internal sealed class PassthroughInternalDriverPreSessionControl :
    IInternalDriverPreSessionControl
{
    private InternalDriverPreSessionSnapshot _snapshot =
        InternalDriverPreSessionSnapshot.Initial with
        {
            State = InternalDriverPreSessionState.Ready,
            Diagnostic = "Test or scripted pre-session control allows session creation.",
            Remediation = "No remediation is required.",
        };

    public InternalDriverPreSessionSnapshot CurrentSnapshot => _snapshot;

    public ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_snapshot);

    public ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
        string leftTrackerSerial,
        string rightTrackerSerial,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<InternalDriverPreSessionSnapshot>(
            new NotSupportedException("The passthrough pre-session control cannot save bindings."));

    public ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<InternalDriverPreSessionSnapshot>(
            new NotSupportedException("The passthrough pre-session control cannot clear bindings."));

    public ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<InternalDriverPreSessionSnapshot>(
            new NotSupportedException("The passthrough pre-session control cannot save policy."));

    public ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
        InternalDriverManualBindingVerificationEvidence verification,
        InternalDriverManualBindingDecision decision,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<InternalDriverPreSessionSnapshot>(
            new NotSupportedException(
                "The passthrough pre-session control cannot save a verification decision."));

    public ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
        CancellationToken cancellationToken = default)
    {
        _snapshot = _snapshot with
        {
            State = InternalDriverPreSessionState.Ready,
            Diagnostic = "Test or scripted pre-session control allows session creation.",
            Remediation = "No remediation is required.",
        };
        return ValueTask.FromResult(_snapshot);
    }

    public ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
        CancellationToken cancellationToken = default)
    {
        _snapshot = _snapshot with
        {
            State = InternalDriverPreSessionState.CleanupSkipped,
            Diagnostic = "Test or scripted controlled stop performed no registration cleanup.",
        };
        return ValueTask.FromResult(_snapshot);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
