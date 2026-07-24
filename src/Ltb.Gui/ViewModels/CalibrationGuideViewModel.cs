using System.Collections.ObjectModel;
using Ltb.App;
using Ltb.Gui.Controls;

namespace Ltb.Gui.ViewModels;

public sealed class CalibrationStepViewModel : ObservableObject
{
    private string _status = "Waiting";

    internal CalibrationStepViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    internal void SetStatus(string status) => Status = status;
}

/// <summary>
/// Presentation-only staged coaching over App-owned capture and solve states.
/// Cue timing is explicitly conceptual and never treated as solver evidence.
/// </summary>
public sealed class CalibrationGuideViewModel : ObservableObject
{
    private static readonly MotionGuideCue[] CaptureCues =
    [
        MotionGuideCue.Pitch,
        MotionGuideCue.Yaw,
        MotionGuideCue.Roll,
        MotionGuideCue.ModerateTranslation,
    ];

    private readonly IGuiTimeSource _timeSource;
    private readonly ObservableCollection<CalibrationStepViewModel> _steps = [];
    private readonly Dictionary<string, CalibrationStepViewModel> _stepByTitle =
        new(StringComparer.Ordinal);
    private bool _isVisible;
    private string _activeHandText = "Preparing calibration";
    private string _cueTitle = "Prepare";
    private string _cueInstruction =
        "Keep both mounts fixed and wait for the application to request one hand.";
    private string _evidenceText = "Actual capture evidence will appear here.";
    private MotionGuideCue _cue = MotionGuideCue.Prepare;
    private bool _isRightHand;
    private string? _activeHandKey;
    private long _activeHandStartedTimestamp;

    internal CalibrationGuideViewModel(IGuiTimeSource timeSource)
    {
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
        Steps = new ReadOnlyObservableCollection<CalibrationStepViewModel>(_steps);
        foreach (var title in new[]
                 {
                     "Left capture",
                     "Right capture",
                     "Associate",
                     "Align",
                     "Solve",
                     "Validate",
                     "Save",
                 })
        {
            var step = new CalibrationStepViewModel(title);
            _steps.Add(step);
            _stepByTitle.Add(title, step);
        }
    }

    public ReadOnlyObservableCollection<CalibrationStepViewModel> Steps { get; }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public string ActiveHandText
    {
        get => _activeHandText;
        private set => SetProperty(ref _activeHandText, value);
    }

    public string CueTitle
    {
        get => _cueTitle;
        private set => SetProperty(ref _cueTitle, value);
    }

    public string CueInstruction
    {
        get => _cueInstruction;
        private set => SetProperty(ref _cueInstruction, value);
    }

    public string EvidenceText
    {
        get => _evidenceText;
        private set => SetProperty(ref _evidenceText, value);
    }

    public MotionGuideCue Cue
    {
        get => _cue;
        private set => SetProperty(ref _cue, value);
    }

    public bool IsRightHand
    {
        get => _isRightHand;
        private set => SetProperty(ref _isRightHand, value);
    }

    internal void Update(InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IsVisible = snapshot.State is >= InternalDriverSessionState.Recording
            and <= InternalDriverSessionState.SaveProfile;
        UpdateSteps(snapshot);
        if (!IsVisible)
        {
            _activeHandKey = null;
            Cue = MotionGuideCue.Prepare;
            return;
        }

        if (snapshot.State == InternalDriverSessionState.Recording)
        {
            UpdateRecording(snapshot);
            return;
        }

        _activeHandKey = null;
        IsRightHand = false;
        ActiveHandText = "Both hands captured";
        Cue = MotionGuideCue.Processing;
        CueTitle = snapshot.State switch
        {
            InternalDriverSessionState.Association => "Associating trackers",
            InternalDriverSessionState.TimeAlignment => "Aligning sample time",
            InternalDriverSessionState.RotationSolve => "Solving mount rotation",
            InternalDriverSessionState.TranslationAttempt => "Checking translation",
            InternalDriverSessionState.Validation => "Validating both hands",
            InternalDriverSessionState.SaveProfile => "Saving validated profiles",
            _ => "Processing calibration",
        };
        CueInstruction = "Hold both mounts fixed. No user motion is required during analysis.";
        EvidenceText = snapshot.Diagnostic;
    }

    private void UpdateRecording(InternalDriverSessionSnapshot snapshot)
    {
        var rightActive = snapshot.Right.Capture is not null;
        var handKey = rightActive ? "right" : "left";
        var now = _timeSource.GetTimestamp();
        if (!string.Equals(_activeHandKey, handKey, StringComparison.Ordinal))
        {
            _activeHandKey = handKey;
            _activeHandStartedTimestamp = now;
        }

        IsRightHand = rightActive;
        ActiveHandText = rightActive ? "Move the right hand only" : "Move the left hand only";
        var elapsed = _timeSource.GetElapsedTime(_activeHandStartedTimestamp, now);
        var cueIndex = Math.Clamp((int)(elapsed.TotalSeconds / 2d), 0, CaptureCues.Length - 1);
        Cue = CaptureCues[cueIndex];
        (CueTitle, CueInstruction) = Cue switch
        {
            MotionGuideCue.Pitch => (
                "Pitch example",
                "Tilt forward and back while continuing varied movement."),
            MotionGuideCue.Yaw => (
                "Yaw example",
                "Turn left and right while keeping the other hand still."),
            MotionGuideCue.Roll => (
                "Roll example",
                "Rotate around the grip axis; keep tracking visible."),
            MotionGuideCue.ModerateTranslation => (
                "Moderate translation example",
                "Move through a comfortable area while continuing gentle multi-axis rotation."),
            _ => ("Move continuously", "Follow the conceptual movement examples."),
        };

        var capture = rightActive ? snapshot.Right.Capture : snapshot.Left.Capture;
        EvidenceText = capture is null
            ? "Waiting for actual capture evidence."
            : $"Actual analyzer: {capture.SampleCount} samples; " +
              $"rotation {capture.RotationProgress:P0}; " +
              $"position tracking availability {capture.PositionProgress:P0}.";
    }

    private void UpdateSteps(InternalDriverSessionSnapshot snapshot)
    {
        var state = snapshot.State;
        var rightCaptureStarted = snapshot.Right.Capture is not null;
        SetCaptureStatus(
            "Left capture",
            state,
            isActive: state == InternalDriverSessionState.Recording && !rightCaptureStarted,
            isComplete: rightCaptureStarted || state > InternalDriverSessionState.Recording);
        SetCaptureStatus(
            "Right capture",
            state,
            isActive: state == InternalDriverSessionState.Recording && rightCaptureStarted,
            isComplete: state > InternalDriverSessionState.Recording);
        SetPhaseStatus("Associate", state, InternalDriverSessionState.Association);
        SetPhaseStatus("Align", state, InternalDriverSessionState.TimeAlignment);
        SetPhaseStatus("Solve", state, InternalDriverSessionState.RotationSolve);
        SetPhaseStatus("Validate", state, InternalDriverSessionState.Validation);
        SetPhaseStatus("Save", state, InternalDriverSessionState.SaveProfile);
    }

    private void SetCaptureStatus(
        string title,
        InternalDriverSessionState state,
        bool isActive,
        bool isComplete)
    {
        _stepByTitle[title].SetStatus(
            isComplete ? "Complete" :
            isActive ? "Current" :
            state < InternalDriverSessionState.Recording ? "Waiting" :
            "Next");
    }

    private void SetPhaseStatus(
        string title,
        InternalDriverSessionState current,
        InternalDriverSessionState target)
    {
        var effectiveTarget = title == "Solve"
            ? InternalDriverSessionState.RotationSolve
            : target;
        _stepByTitle[title].SetStatus(
            current > effectiveTarget ? "Complete" :
            current == effectiveTarget ||
            (title == "Solve" && current == InternalDriverSessionState.TranslationAttempt)
                ? "Current"
                : "Waiting");
    }
}
