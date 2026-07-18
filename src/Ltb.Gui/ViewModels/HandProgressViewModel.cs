using System.Globalization;
using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Binding surface for one hand's capture coverage. All numbers are produced
/// by the wizard's <see cref="CalibrationWizardCaptureProgress"/> events; the
/// view model renders them and owns no coverage policy.
/// </summary>
public sealed class HandProgressViewModel : ObservableObject
{
    private int _sampleCount;
    private double _rotationProgress;
    private double _positionProgress;
    private double _motionAxisCoverage;
    private bool _rotationReady;
    private bool _positionReady;
    private string _diagnostic = "no capture yet";

    public HandProgressViewModel(CalibrationWizardHand hand)
    {
        Hand = hand;
        Title = hand == CalibrationWizardHand.Left ? "Left hand" : "Right hand";
    }

    public CalibrationWizardHand Hand { get; }

    public string Title { get; }

    public int SampleCount
    {
        get => _sampleCount;
        private set => SetProperty(ref _sampleCount, value);
    }

    public double RotationProgress
    {
        get => _rotationProgress;
        private set => SetProperty(ref _rotationProgress, value);
    }

    public double PositionProgress
    {
        get => _positionProgress;
        private set => SetProperty(ref _positionProgress, value);
    }

    public double MotionAxisCoverage
    {
        get => _motionAxisCoverage;
        private set => SetProperty(ref _motionAxisCoverage, value);
    }

    public bool RotationReady
    {
        get => _rotationReady;
        private set => SetProperty(ref _rotationReady, value);
    }

    public bool PositionReady
    {
        get => _positionReady;
        private set => SetProperty(ref _positionReady, value);
    }

    public string Diagnostic
    {
        get => _diagnostic;
        private set => SetProperty(ref _diagnostic, value);
    }

    public string CoverageSummary
    {
        get => string.Create(
            CultureInfo.InvariantCulture,
            $"samples={SampleCount} axis_coverage={MotionAxisCoverage:F4} " +
            $"rotation_ready={FormatFlag(RotationReady)} position_ready={FormatFlag(PositionReady)}");
    }

    public void Update(CalibrationWizardCaptureProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Hand != Hand)
        {
            throw new ArgumentException(
                $"Progress for {progress.Hand} was routed to the {Hand} panel.",
                nameof(progress));
        }

        SampleCount = progress.SampleCount;
        RotationProgress = progress.RotationProgress;
        PositionProgress = progress.PositionProgress;
        MotionAxisCoverage = progress.MotionAxisCoverage;
        RotationReady = progress.RotationReady;
        PositionReady = progress.PositionReady;
        Diagnostic = progress.Diagnostic;
        OnPropertyChanged(nameof(CoverageSummary));
    }

    public void Reset()
    {
        SampleCount = 0;
        RotationProgress = 0d;
        PositionProgress = 0d;
        MotionAxisCoverage = 0d;
        RotationReady = false;
        PositionReady = false;
        Diagnostic = "no capture yet";
        OnPropertyChanged(nameof(CoverageSummary));
    }

    private static string FormatFlag(bool value) => value.ToString().ToLowerInvariant();
}
