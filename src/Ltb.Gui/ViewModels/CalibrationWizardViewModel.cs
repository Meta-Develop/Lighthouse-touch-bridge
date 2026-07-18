using System.Collections.ObjectModel;
using System.Globalization;
using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Binding surface for one wizard session. It validates editable launch input
/// and renders <see cref="ICalibrationWizardOutput"/> events; wizard sequencing,
/// device policy, native composition, and cleanup remain behind the selected
/// session implementation.
/// </summary>
public sealed class CalibrationWizardViewModel :
    ObservableObject,
    ICalibrationWizardOutput,
    IDisposable
{
    private readonly ICalibrationWizardSessionFactory _sessionFactory;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _abortSource;
    private TaskCompletionSource? _runCompletion;
    private CalibrationWizardMode _mode;
    private string _profileStorePath;
    private string _leftVmtSlot;
    private string _rightVmtSlot;
    private string _steamVrSettingsPath;
    private string _captureDurationSeconds;
    private string _captureRateHz;
    private string _logPath;
    private string _monitorRateHz;
    private string _reconnectDelaySeconds;
    private CalibrationWizardState _currentState = CalibrationWizardState.Stopped;
    private string _currentDiagnostic;
    private string _resultSummary = string.Empty;
    private bool _isRunning;

    public CalibrationWizardViewModel(
        ICalibrationWizardSessionFactory sessionFactory,
        GuiCommandLineOptions options,
        string? startupDiagnostic = null,
        Action<Action>? dispatch = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        ArgumentNullException.ThrowIfNull(options);
        _dispatch = dispatch ?? (action => action());
        _mode = options.Mode;
        _profileStorePath = options.ProfileStorePath;
        _leftVmtSlot = options.LeftVmtSlot;
        _rightVmtSlot = options.RightVmtSlot;
        _steamVrSettingsPath = options.SteamVrSettingsPath;
        _captureDurationSeconds = options.CaptureDurationSeconds;
        _captureRateHz = options.CaptureRateHz;
        _logPath = options.LogPath;
        _monitorRateHz = options.MonitorRateHz;
        _reconnectDelaySeconds = options.ReconnectDelaySeconds;
        _currentDiagnostic = startupDiagnostic ?? ReadyDiagnostic(options.Mode);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning);
        AbortCommand = new RelayCommand(Abort, () => IsRunning);
    }

    public bool IsScriptedDemoMode
    {
        get => Mode == CalibrationWizardMode.ScriptedDemo;
        set
        {
            if (value)
            {
                Mode = CalibrationWizardMode.ScriptedDemo;
            }
        }
    }

    public bool IsProductionMode
    {
        get => Mode == CalibrationWizardMode.Production;
        set
        {
            if (value)
            {
                Mode = CalibrationWizardMode.Production;
            }
        }
    }

    public string ProfileStorePath
    {
        get => _profileStorePath;
        set => SetProperty(ref _profileStorePath, value);
    }

    public string LeftVmtSlot
    {
        get => _leftVmtSlot;
        set => SetProperty(ref _leftVmtSlot, value);
    }

    public string RightVmtSlot
    {
        get => _rightVmtSlot;
        set => SetProperty(ref _rightVmtSlot, value);
    }

    public string SteamVrSettingsPath
    {
        get => _steamVrSettingsPath;
        set => SetProperty(ref _steamVrSettingsPath, value);
    }

    public string CaptureDurationSeconds
    {
        get => _captureDurationSeconds;
        set => SetProperty(ref _captureDurationSeconds, value);
    }

    public string CaptureRateHz
    {
        get => _captureRateHz;
        set => SetProperty(ref _captureRateHz, value);
    }

    public string LogPath
    {
        get => _logPath;
        set => SetProperty(ref _logPath, value);
    }

    public string MonitorRateHz
    {
        get => _monitorRateHz;
        set => SetProperty(ref _monitorRateHz, value);
    }

    public string ReconnectDelaySeconds
    {
        get => _reconnectDelaySeconds;
        set => SetProperty(ref _reconnectDelaySeconds, value);
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
                OnPropertyChanged(nameof(CanEditConfiguration));
            }
        }
    }

    public bool CanEditConfiguration => !IsRunning;

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

        if (!TryCreateSession(out var session, out var validationDiagnostic))
        {
            var message = $"configuration_error: {validationDiagnostic}";
            CurrentDiagnostic = validationDiagnostic!;
            ResultSummary = message;
            Events.Clear();
            Events.Add(message);
            return;
        }

        var previousAbortSource = _abortSource;
        var abortSource = new CancellationTokenSource();
        var runCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _abortSource = abortSource;
        _runCompletion = runCompletion;
        previousAbortSource?.Dispose();
        IsRunning = true;
        CurrentState = CalibrationWizardState.Stopped;
        CurrentDiagnostic = Mode == CalibrationWizardMode.Production
            ? "starting the production wizard session"
            : "starting the scripted wizard session";
        ResultSummary = string.Empty;
        Events.Clear();
        LeftHand.Reset();
        RightHand.Reset();
        try
        {
            var result = await Task
                .Run(() => session!.RunAsync(this, abortSource.Token))
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
            _dispatch(() =>
            {
                IsRunning = false;
                runCompletion.TrySetResult();
            });
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

    public async Task StopAsync()
    {
        Abort();
        var completion = _runCompletion?.Task;
        if (completion is not null)
        {
            await completion.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Abort();
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

    private CalibrationWizardMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsScriptedDemoMode));
            OnPropertyChanged(nameof(IsProductionMode));
            if (!IsRunning)
            {
                CurrentDiagnostic = ReadyDiagnostic(value);
            }
        }
    }

    private bool TryCreateSession(
        out ICalibrationWizardSession? session,
        out string? diagnostic)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(ProfileStorePath))
        {
            diagnostic = "--profiles requires a non-empty profile-store path.";
            return false;
        }

        var logPath = string.IsNullOrWhiteSpace(LogPath) ? null : LogPath;
        if (Mode == CalibrationWizardMode.ScriptedDemo)
        {
            session = _sessionFactory.CreateScripted(ProfileStorePath, logPath);
            diagnostic = null;
            return true;
        }

        if (!int.TryParse(LeftVmtSlot, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left))
        {
            diagnostic = "--left-vmt-slot requires an integer.";
            return false;
        }

        if (!int.TryParse(RightVmtSlot, NumberStyles.Integer, CultureInfo.InvariantCulture, out var right))
        {
            diagnostic = "--right-vmt-slot requires an integer.";
            return false;
        }

        if (!TryParseDouble(CaptureDurationSeconds, "--duration", out var duration, out diagnostic) ||
            !TryParseDouble(CaptureRateHz, "--rate", out var rate, out diagnostic) ||
            !TryParseDouble(MonitorRateHz, "--monitor-rate", out var monitorRate, out diagnostic) ||
            !TryParseDouble(
                ReconnectDelaySeconds,
                "--reconnect-delay",
                out var reconnectDelay,
                out diagnostic))
        {
            return false;
        }

        var options = new ProductionCalibrationWizardSessionOptions
        {
            ProfileStorePath = ProfileStorePath,
            LeftVmtSlot = left,
            RightVmtSlot = right,
            SteamVrSettingsPath = SteamVrSettingsPath,
            CaptureDurationSeconds = duration,
            CaptureRateHz = rate,
            LogPath = logPath,
            MonitorRateHz = monitorRate,
            ReconnectDelaySeconds = reconnectDelay,
        };
        if (!options.TryValidate(out diagnostic))
        {
            return false;
        }

        session = _sessionFactory.CreateProduction(options);
        return true;
    }

    private static bool TryParseDouble(
        string value,
        string option,
        out double parsed,
        out string? diagnostic)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed))
        {
            diagnostic = $"{option} requires a number.";
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static string ReadyDiagnostic(CalibrationWizardMode mode) => mode switch
    {
        CalibrationWizardMode.ScriptedDemo =>
            "press Start to run the deterministic scripted two-hand wizard",
        CalibrationWizardMode.Production =>
            "review production paths and runtime parameters, then press Start",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

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
