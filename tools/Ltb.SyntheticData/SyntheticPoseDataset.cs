using Ltb.Core;

namespace Ltb.SyntheticData;

/// <summary>
/// A deterministic calibration fixture with raw lagged streams and truth-aligned
/// pairs. The aligned pairs deliberately use a shared truth timestamp: Milestone 0
/// validates offline calibration after alignment, not lag estimation.
/// </summary>
public sealed record SyntheticPoseDataset(
    SyntheticScenario Scenario,
    int Seed,
    RigidTransform GroundTruthMount,
    RigidTransform QuestFromLighthouse,
    double RequestedLagSeconds,
    double KnownLagSeconds,
    int RequestedSampleCount,
    int DroppedSampleCount,
    SyntheticInjectionSummary Injections,
    IReadOnlyList<TimestampedPoseSample> RawTrackerSamples,
    IReadOnlyList<TimestampedPoseSample> RawControllerSamples,
    IReadOnlyList<SynchronizedPosePair> AlignedPairs)
{
    public const string AlignmentContract =
        "Raw controller timestamps include RequestedLagSeconds, whose generated truth is KnownLagSeconds; AlignedPairs share the tracker truth timestamp after known-lag alignment.";
}

/// <summary>Counts injected measurement artifacts that survived sample dropping.</summary>
public sealed record SyntheticInjectionSummary(
    int OutlierPoseCount,
    int QuaternionSignFlipCount,
    int TrackingInvalidSampleCount,
    int ControllerPositionInvalidSampleCount);
