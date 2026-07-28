using System.Collections.ObjectModel;
using Ltb.App;

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
                VerificationStatusText = decision ==
                    InternalDriverManualBindingDecision.AcceptCorrectionCandidate
                        ? "Correction accepted explicitly and saved for the next preflight."
                        : "Authoritative manual binding retained explicitly.";
                _verification = null;
                HasCorrectionChoice = false;
                NotifyCommandAvailabilityChanged();
            });
        }
        catch (Exception exception)
        {
            _dispatch(() => PresentUnexpectedFailure(
                $"Manual-binding verification decision was not saved: {exception.Message}"));
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
        NotifyCommandAvailabilityChanged();
    }

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
