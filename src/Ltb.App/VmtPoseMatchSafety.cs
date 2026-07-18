using System.Numerics;
using Ltb.Core;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>
/// Single source of truth for the VMT pose-match fail-safe. Every command path
/// (bridge, daily, wizard) must reject the same time-skew, position, and
/// rotation envelopes; callers vary only the exception type they throw.
/// </summary>
internal static class VmtPoseMatchSafety
{
    internal const double MaximumPoseComparisonSkewSeconds = 0.05d;
    internal const float MaximumVmtPositionErrorMeters = 0.15f;
    internal const float MaximumVmtRotationErrorRadians = MathF.PI / 9f;

    internal static void EnsureVmtPoseMatchesMount(
        PoseSourceSample trackerSample,
        PoseSourceSample vmtSample,
        RigidTransform mount,
        Func<string, Exception> createFailure)
    {
        var skewSeconds = Math.Abs(
            trackerSample.MonotonicHostTimeSeconds -
            vmtSample.MonotonicHostTimeSeconds);
        if (!double.IsFinite(skewSeconds) ||
            skewSeconds > MaximumPoseComparisonSkewSeconds)
        {
            throw createFailure(
                $"Tracker/VMT pose samples are not comparable in time (skew {skewSeconds:R}s).");
        }

        var expected = CoordinateConventions.ComposeRuntimeOutput(
            trackerSample.Pose,
            mount);
        var positionError = Vector3.Distance(
            expected.TranslationMeters,
            vmtSample.Pose.TranslationMeters);
        var quaternionDot = Math.Clamp(
            MathF.Abs(Quaternion.Dot(expected.Rotation, vmtSample.Pose.Rotation)),
            0f,
            1f);
        var rotationError = 2f * MathF.Acos(quaternionDot);
        if (positionError > MaximumVmtPositionErrorMeters ||
            rotationError > MaximumVmtRotationErrorRadians)
        {
            throw createFailure(
                "VMT output pose does not match tracker * mount " +
                $"(position error {positionError:R}m, rotation error {rotationError:R}rad).");
        }
    }
}
