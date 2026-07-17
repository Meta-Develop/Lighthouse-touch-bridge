using Ltb.Core;

namespace Ltb.SyntheticData;

internal static class SyntheticRecordingFactory
{
    public const string TrackerStreamId = "tracker";

    public const string ControllerStreamId = "controller";

    public static PoseRecording Create(SyntheticPoseDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var tracker = new PoseStreamRecording(
            new PoseStreamIdentity(
                TrackerStreamId,
                PoseSourceKind.TrackedPose,
                "synthetic-tracker-001",
                "Synthetic Lighthouse tracker"),
            dataset.RawTrackerSamples.Select(ToRecordedSample));
        var controller = new PoseStreamRecording(
            new PoseStreamIdentity(
                ControllerStreamId,
                PoseSourceKind.InputController,
                "synthetic-controller-001",
                "Synthetic input controller"),
            dataset.RawControllerSamples.Select(ToRecordedSample));

        return new PoseRecording([tracker, controller]);
    }

    private static RecordedPoseSample ToRecordedSample(TimestampedPoseSample sample) => new(
        sample,
        isConnected: true,
        sample.IsTrackingValid
            ? PoseTrackingResult.RunningOk
            : PoseTrackingResult.RunningOutOfRange);
}
