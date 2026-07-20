using System;
using System.Collections.Generic;
using System.Linq;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.MetaLink;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>
/// Per-sample evidence for mapping a hand-specific LibOVR pose timestamp onto
/// the application monotonic clock. This remains attached to a calibration
/// capture instead of being discarded when the portable solver input is built.
/// </summary>
internal readonly record struct MetaLinkCalibrationClockEvidence(
    double RawMetaTimeSeconds,
    double AppMonotonicTimeSeconds,
    long AppMonotonicTimeNanoseconds,
    double ClockUncertaintySeconds);

/// <summary>
/// Immutable first-party capture for one already-associated hand and physical
/// tracker. Complete Meta samples and raw OpenVR tracker samples are retained
/// as evidence; only runtime-neutral pose samples cross into the calibration
/// library.
/// </summary>
internal sealed class MetaLinkCalibrationCapture
{
    private readonly IReadOnlyList<MetaLinkControllerSnapshot> _metaSamples;
    private readonly IReadOnlyList<PoseSourceSample> _rawTrackerSamples;
    private readonly IReadOnlyList<TimestampedPoseSample> _controllerPoseSamples;
    private readonly IReadOnlyList<TimestampedPoseSample> _trackerPoseSamples;
    private readonly IReadOnlyList<MetaLinkCalibrationClockEvidence> _clockEvidence;

    public MetaLinkCalibrationCapture(
        MetaLinkHand hand,
        string trackerSerial,
        IEnumerable<MetaLinkControllerSnapshot> metaSamples,
        IEnumerable<PoseSourceSample> rawTrackerSamples)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentNullException.ThrowIfNull(metaSamples);
        ArgumentNullException.ThrowIfNull(rawTrackerSamples);

        var retainedMetaSamples = metaSamples.ToArray();
        if (retainedMetaSamples.Any(sample => sample is null))
        {
            throw new ArgumentException("Meta samples cannot contain null entries.", nameof(metaSamples));
        }

        if (retainedMetaSamples.Any(sample => sample.Hand != hand))
        {
            throw new ArgumentException(
                "Every Meta calibration sample must belong to the capture hand.",
                nameof(metaSamples));
        }

        var retainedTrackerSamples = rawTrackerSamples.ToArray();
        Hand = hand;
        TrackerSerial = trackerSerial;
        _metaSamples = Array.AsReadOnly(retainedMetaSamples);
        _rawTrackerSamples = Array.AsReadOnly(retainedTrackerSamples);
        _controllerPoseSamples = Array.AsReadOnly(
            retainedMetaSamples.Select(ToControllerPoseSample).ToArray());
        _trackerPoseSamples = Array.AsReadOnly(
            retainedTrackerSamples.Select(ToTrackerPoseSample).ToArray());
        _clockEvidence = Array.AsReadOnly(
            retainedMetaSamples
                .Select(sample => new MetaLinkCalibrationClockEvidence(
                    sample.Pose.RawMetaTimeSeconds,
                    sample.Pose.AppMonotonicTimeSeconds,
                    sample.Pose.AppMonotonicTimeNanoseconds,
                    sample.Pose.ClockUncertaintySeconds))
                .ToArray());
    }

    public MetaLinkHand Hand { get; }

    public string TrackerSerial { get; }

    /// <summary>
    /// Complete input/pose observations. No synthetic SteamVR controller
    /// descriptor or persistent Meta controller identity is introduced.
    /// </summary>
    public IReadOnlyList<MetaLinkControllerSnapshot> MetaSamples => _metaSamples;

    /// <summary>
    /// Original raw/uncalibrated OpenVR observations, including source timing,
    /// connectivity, tracking result, age, and velocity metadata.
    /// </summary>
    public IReadOnlyList<PoseSourceSample> RawTrackerSamples => _rawTrackerSamples;

    public IReadOnlyList<TimestampedPoseSample> ControllerPoseSamples =>
        _controllerPoseSamples;

    public IReadOnlyList<TimestampedPoseSample> TrackerPoseSamples =>
        _trackerPoseSamples;

    public IReadOnlyList<MetaLinkCalibrationClockEvidence> ClockEvidence =>
        _clockEvidence;

    public double MaximumClockUncertaintySeconds => _clockEvidence.Count == 0
        ? double.NaN
        : _clockEvidence.Max(evidence => evidence.ClockUncertaintySeconds);

    public HandCalibrationInput ToCalibrationInput() => new(
        ToCalibrationHand(Hand),
        TrackerSerial,
        TrackerPoseSamples,
        ControllerPoseSamples);

    internal static TimestampedPoseSample ToControllerPoseSample(
        MetaLinkControllerSnapshot sample)
    {
        var pose = sample.Pose;
        var validity = PoseValidity.None;
        if (pose.HasValidOrientation && pose.IsOrientationTracked)
        {
            validity |= PoseValidity.Orientation | PoseValidity.TrackingValid;
        }

        if (pose.HasValidPosition && pose.IsPositionTracked)
        {
            validity |= PoseValidity.Position;
        }

        return new TimestampedPoseSample(
            pose.AppMonotonicTimeSeconds,
            pose.TrackingOriginFromController,
            validity);
    }

    private static TimestampedPoseSample ToTrackerPoseSample(PoseSourceSample sample)
    {
        var validity = sample.Validity;
        if (!sample.IsConnected)
        {
            validity &= ~PoseValidity.TrackingValid;
        }

        return new TimestampedPoseSample(
            sample.MonotonicHostTimeSeconds,
            sample.Pose,
            validity);
    }

    internal static CalibrationHand ToCalibrationHand(MetaLinkHand hand) => hand switch
    {
        MetaLinkHand.Left => CalibrationHand.Left,
        MetaLinkHand.Right => CalibrationHand.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };
}

/// <summary>
/// Retains only real, strictly increasing Meta pose observations and derives
/// portable motion coverage without inventing timestamps for repeated polls.
/// </summary>
internal sealed class InternalDriverCaptureEvidenceTracker
{
    private readonly MetaLinkHand _hand;
    private readonly List<TimestampedPoseSample> _samples = [];

    public InternalDriverCaptureEvidenceTracker(MetaLinkHand hand)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        _hand = hand;
    }

    public bool TryAppend(MetaLinkRuntimeSnapshot observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var timestamp = observation.ObservedAtMonotonicSeconds;
        if (_samples.Count > 0 && timestamp <= _samples[^1].MonotonicTimeSeconds)
        {
            return false;
        }

        var hand = observation.ForHand(_hand);
        TimestampedPoseSample sample;
        if (hand.Readiness == MetaLinkReadiness.Ready && hand.Controller is { } controller)
        {
            var pose = MetaLinkCalibrationCapture.ToControllerPoseSample(controller);
            sample = new TimestampedPoseSample(timestamp, pose.Pose, pose.Validity);
        }
        else
        {
            sample = new TimestampedPoseSample(
                timestamp,
                RigidTransform.Identity,
                PoseValidity.None);
        }

        _samples.Add(sample);
        return true;
    }

    public InternalDriverCaptureEvidence Evaluate()
    {
        var coverage = MotionCoverageAnalyzer.Evaluate(_samples);
        return new InternalDriverCaptureEvidence(
            coverage.SampleCount,
            coverage.TrackingValidityFraction,
            coverage.OrientationValidityFraction,
            coverage.PositionValidityFraction,
            coverage.RotationAxisCoverage,
            coverage.TotalRotationDegrees,
            coverage.RotationProgress,
            coverage.PositionProgress,
            coverage.IsRotationSufficient,
            coverage.IsPositionSufficient);
    }
}

/// <summary>
/// Keeps solver input on real mapped per-hand pose timestamps while explicitly
/// discarding duplicate or regressing source/app timestamp pairs.
/// </summary>
internal sealed class InternalDriverMappedMetaSampleFilter
{
    private readonly MetaLinkHand _hand;
    private readonly List<MetaLinkControllerSnapshot> _samples = [];
    private double? _lastRawMetaTimeSeconds;

    public InternalDriverMappedMetaSampleFilter(MetaLinkHand hand)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        _hand = hand;
    }

    public IReadOnlyList<MetaLinkControllerSnapshot> Samples => _samples;

    public bool TryAppend(MetaLinkControllerSnapshot sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Hand != _hand)
        {
            throw new ArgumentException(
                $"Mapped samples for {_hand} cannot accept a {sample.Hand} sample.",
                nameof(sample));
        }

        var rawTime = sample.Pose.RawMetaTimeSeconds;
        var appTime = sample.Pose.AppMonotonicTimeSeconds;
        if (!double.IsFinite(rawTime) || !double.IsFinite(appTime) ||
            _lastRawMetaTimeSeconds is { } lastRaw && rawTime <= lastRaw ||
            _samples.Count > 0 && appTime <= _samples[^1].Pose.AppMonotonicTimeSeconds)
        {
            return false;
        }

        _samples.Add(sample);
        _lastRawMetaTimeSeconds = rawTime;
        return true;
    }
}
