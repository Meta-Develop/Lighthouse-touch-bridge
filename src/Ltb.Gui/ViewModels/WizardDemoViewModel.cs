using System.Collections.ObjectModel;
using System.Globalization;
using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Binding surface for one wizard session. It implements
/// <see cref="ICalibrationWizardOutput"/> so the existing UI-neutral wizard
/// drives every state, coverage, and event value shown by the window; the
/// view model renders those events and owns no sequencing or device policy.
/// Wizard events may arrive on a worker thread, so every mutation is routed
/// through the injected dispatch delegate (the Avalonia UI-thread dispatcher
/// in the application, an immediate call in tests).
/// </summary>
public sealed class WizardDemoViewModel :
    ObservableObject,
    ICalibrationWizardOutput,
    IDisposable
{
    private readonly ICalibrationWizardSession _session;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _abortSource;
    private CalibrationWizardState _currentState = CalibrationWizardState.Stopped;
    private string _currentDiagnostic = "press Start to run the scripted two-hand wizard";
    private string _resultSummary = string.Empty;
    private bool _isRunning;

    public WizardDemoViewModel(
        ICalibrationWizardSession session,
        Action<Action>? dispatch = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatch = dispatch ?? (action => action());
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning);
        AbortCommand = new RelayCommand(Abort, () => IsRunning);
    }

    public CalibrationWizardState CurrentState
    {
        get => _currentState;
        private set => SetProperty(ref _currentState, value);
    }

    public string CurrentDiagnostic
    {
        get => _currentDiagnostic;
        private set => SetProperty(ref _currentDiagnostic, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                AbortCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public HandProgressViewModel LeftHand { get; } =
        new(CalibrationWizardHand.Left);

    public HandProgressViewModel RightHand { get; } =
        new(CalibrationWizardHand.Right);

    public ObservableCollection<string> Events { get; } = [];

    public RelayCommand StartCommand { get; }

    public RelayCommand AbortCommand { get; }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var previousAbortSource = _abortSource;
        var abortSource = new CancellationTokenSource();
        _abortSource = abortSource;
        previousAbortSource?.Dispose();
        IsRunning = true;
        CurrentState = CalibrationWizardState.Stopped;
        CurrentDiagnostic = "starting the scripted wizard session";
        ResultSummary = string.Empty;
        Events.Clear();
        LeftHand.Reset();
        RightHand.Reset();
        try
        {
            var result = await Task
                .Run(() => _session.RunAsync(this, abortSource.Token))
                .ConfigureAwait(false);
            _dispatch(() => ResultSummary = Summarize(result));
        }
        catch (OperationCanceledException)
        {
            _dispatch(() => ResultSummary = "wizard_result: cancelled before completion");
        }
        catch (Exception exception)
        {
            var message = $"wizard_error: {exception.Message}";
            _dispatch(() =>
            {
                ResultSummary = message;
                Events.Add(message);
            });
        }
        finally
        {
            _dispatch(() => IsRunning = false);
        }
    }

    public void Abort()
    {
        try
        {
            _abortSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // An abort that races run teardown has nothing left to cancel.
        }
    }

    public void Dispose()
    {
        _abortSource?.Dispose();
        _abortSource = null;
    }

    void ICalibrationWizardOutput.OnStateChanged(
        CalibrationWizardState state,
        string diagnostic) =>
        _dispatch(() =>
        {
            CurrentState = state;
            CurrentDiagnostic = diagnostic;
            Events.Add($"state: {state} ({diagnostic})");
        });

    void ICalibrationWizardOutput.OnCaptureProgress(
        CalibrationWizardCaptureProgress progress) =>
        _dispatch(() =>
        {
            var hand = progress.Hand == CalibrationWizardHand.Left
                ? LeftHand
                : RightHand;
            hand.Update(progress);
            Events.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"coverage: hand={progress.Hand.ToString().ToLowerInvariant()} {hand.CoverageSummary}"));
        });

    void ICalibrationWizardOutput.WriteLine(string message) =>
        _dispatch(() => Events.Add(message));

    private static string Summarize(CalibrationWizardResult result)
    {
        var status = result.Success
            ? "success"
            : result.Cancelled ? "cancelled" : "failed";
        var path = result.ReusedProfiles ? "later-run-reuse" : "first-run-capture";
        return $"wizard_result: {status} profile_path={path} " +
            $"final_state={result.FinalState} diagnostic={result.Diagnostic}";
    }
}
