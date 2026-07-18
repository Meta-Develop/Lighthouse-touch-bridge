using System.Globalization;
using System.Numerics;
using System.Text;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.SyntheticData;

public sealed record CalibrationConsoleReport(string Text, bool IsFailure)
{
    public static CalibrationConsoleReport Create(
        SyntheticPoseDataset dataset,
        CalibrationResult result)
    {
        var rotationErrorDegrees = result.Success
            ? RotationErrorDegrees(dataset.GroundTruthMount.Rotation, result.TrackerToController.Rotation)
            : double.NaN;
        var translationErrorMillimeters = result.SelectedModel == CalibrationModel.FullSixDof
            ? 1_000.0 * Vector3.Distance(
                dataset.GroundTruthMount.TranslationMeters,
                result.TrackerToController.TranslationMeters)
            : (double?)null;
        var verdict = result.SelectedModel switch
        {
            CalibrationModel.Failed => "FAIL",
            CalibrationModel.RotationOnly when result.RequestedPolicy == CalibrationPolicy.Auto => "FALLBACK",
            _ => "PASS",
        };

        var output = new StringBuilder();
        output.AppendLine("Lighthouse Touch Bridge - Synthetic Calibration Report");
        output.AppendLine(CultureInfo.InvariantCulture, $"verdict: {verdict}");
        output.AppendLine(CultureInfo.InvariantCulture, $"scenario: {ScenarioName(dataset.Scenario)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"seed: {dataset.Seed}");
        output.AppendLine(CultureInfo.InvariantCulture, $"requested_policy: {PolicyName(result.RequestedPolicy)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"selected_model: {ModelName(result.SelectedModel)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"selection_reason: {result.SelectionReason}");
        output.AppendLine(CultureInfo.InvariantCulture, $"requested_lag_ms: {Format(dataset.RequestedLagSeconds * 1_000.0)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"known_lag_ms: {Format(dataset.KnownLagSeconds * 1_000.0)}");
        output.AppendLine("alignment: known-lag truth alignment (lag estimation is Milestone 1)");
        output.AppendLine(CultureInfo.InvariantCulture, $"ground_truth_mount: {FormatTransform(dataset.GroundTruthMount)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"estimated_mount: {(result.Success ? FormatTransform(result.TrackerToController) : "n/a")}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_error_deg: {Format(rotationErrorDegrees)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_error_mm: {FormatNullable(translationErrorMillimeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_rms_deg: {Format(result.Quality.RotationRmsDegrees)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_p95_deg: {Format(result.Quality.RotationPercentileDegrees)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_inlier_ratio: {Format(result.Quality.RotationInlierRatio)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"position_rms_mm: {FormatNullable(result.Quality.PositionRmsMillimeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"position_p95_mm: {FormatNullable(result.Quality.PositionPercentileMillimeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_only_position_rms_mm: {FormatNullable(result.Quality.RotationOnlyPositionRmsMillimeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_magnitude_m: {Format(result.Quality.TranslationMagnitudeMeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_inlier_ratio: {Format(result.Quality.TranslationInlierRatio)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_split_disagreement_mm: {Format(result.Quality.TranslationSplitDisagreementMillimeters)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_observable: {Lower(result.Motion.RotationObservable)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_observable: {Lower(result.Motion.TranslationObservable)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_degeneracy: {result.Motion.RotationDegeneracy}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_degeneracy: {result.Motion.TranslationDegeneracy}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_axis_coverage: {Format(result.Motion.RotationAxisCoverage)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"translation_condition: {Format(result.Motion.TranslationConditionNumber)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"requested_samples: {dataset.RequestedSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"dropped_samples: {dataset.DroppedSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"injected_outlier_poses: {dataset.Injections.OutlierPoseCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"injected_quaternion_sign_flips: {dataset.Injections.QuaternionSignFlipCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"tracking_invalid_samples: {dataset.Injections.TrackingInvalidSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"controller_position_invalid_samples: {dataset.Injections.ControllerPositionInvalidSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"aligned_pairs: {dataset.AlignedPairs.Count}");
        output.AppendLine(CultureInfo.InvariantCulture, $"solver_samples: {result.SampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"rotation_valid_samples: {result.RotationValidSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"position_valid_samples: {result.PositionValidSampleCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"motion_pairs: {result.MotionPairCount}");
        output.AppendLine(CultureInfo.InvariantCulture, $"validation_motion_pairs: {result.ValidationMotionPairCount}");
        return new CalibrationConsoleReport(output.ToString(), verdict == "FAIL");
    }

    private static double RotationErrorDegrees(Quaternion expected, Quaternion actual)
    {
        var numerator = Math.Abs(
            ((double)expected.X * actual.X) +
            ((double)expected.Y * actual.Y) +
            ((double)expected.Z * actual.Z) +
            ((double)expected.W * actual.W));
        var expectedNormSquared =
            ((double)expected.X * expected.X) +
            ((double)expected.Y * expected.Y) +
            ((double)expected.Z * expected.Z) +
            ((double)expected.W * expected.W);
        var actualNormSquared =
            ((double)actual.X * actual.X) +
            ((double)actual.Y * actual.Y) +
            ((double)actual.Z * actual.Z) +
            ((double)actual.W * actual.W);
        var denominator = Math.Sqrt(expectedNormSquared * actualNormSquared);
        var dot = denominator == 0.0
            ? 0.0
            : Math.Clamp(numerator / denominator, 0.0, 1.0);
        return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static string FormatTransform(RigidTransform transform) => transform.IsValid
        ? $"t_m=[{Format(transform.TranslationMeters.X)}, {Format(transform.TranslationMeters.Y)}, {Format(transform.TranslationMeters.Z)}] " +
          $"q_xyzw=[{Format(transform.Rotation.X)}, {Format(transform.Rotation.Y)}, {Format(transform.Rotation.Z)}, {Format(transform.Rotation.W)}]"
        : "n/a";

    private static string Format(double value) => double.IsFinite(value)
        ? value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
        : "n/a";

    private static string FormatNullable(double? value) => value.HasValue ? Format(value.Value) : "n/a";

    private static string Lower(bool value) => value ? "true" : "false";

    private static string ScenarioName(SyntheticScenario scenario) => scenario switch
    {
        SyntheticScenario.SingleAxisRotation => "single-axis",
        SyntheticScenario.PureTranslation => "pure-translation",
        SyntheticScenario.TranslationDegenerate => "translation-degenerate",
        _ => scenario.ToString().ToLowerInvariant(),
    };

    private static string PolicyName(CalibrationPolicy policy) => policy switch
    {
        CalibrationPolicy.RotationOnly => "rotation-only",
        CalibrationPolicy.FullSixDof => "full-6dof",
        _ => "auto",
    };

    private static string ModelName(CalibrationModel model) => model switch
    {
        CalibrationModel.RotationOnly => "rotation-only",
        CalibrationModel.FullSixDof => "full-6dof",
        _ => "failed",
    };
}
