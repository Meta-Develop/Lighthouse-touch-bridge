using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverPerHandCalibrationTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(InternalDriverSessionIntent.NormalStart, InternalDriverCalibrationHandSet.None)]
    [InlineData(InternalDriverSessionIntent.Calibrate, InternalDriverCalibrationHandSet.Both)]
    [InlineData(InternalDriverSessionIntent.CalibrateLeft, InternalDriverCalibrationHandSet.Left)]
    [InlineData(InternalDriverSessionIntent.CalibrateRight, InternalDriverCalibrationHandSet.Right)]
    public void SessionIntentSelectsNormalLeftRightOrBoth(
        InternalDriverSessionIntent intent,
        InternalDriverCalibrationHandSet expected)
    {
        var options = new InternalDriverSessionOptions { Intent = intent };

        options.Validate();

        Assert.Equal(expected, options.RequestedCalibrationHands);
    }

    [Fact]
    public void ExplicitCalibrateCanSelectOneHandWithoutChangingLegacyBothDefault()
    {
        var defaultBoth = new InternalDriverSessionOptions
        {
            Intent = InternalDriverSessionIntent.Calibrate,
        };
        var selectedLeft = defaultBoth with
        {
            CalibrationHands = InternalDriverCalibrationHandSet.Left,
        };

        Assert.Equal(
            InternalDriverCalibrationHandSet.Both,
            defaultBoth.RequestedCalibrationHands);
        Assert.Equal(
            InternalDriverCalibrationHandSet.Left,
            selectedLeft.RequestedCalibrationHands);
    }

    [Fact]
    public void SingleHandAssociationScoresCompleteRosterAndPreservesOppositeTracker()
    {
        var capture = ScriptedCapture(CalibrationWizardHand.Left);

        var result = InternalDriverSingleHandAssociator.Associate(
            capture,
            ScriptedCalibrationWizardRuntime.RightTrackerSerial);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(
            ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            result.Assignment!.TrackerSerial);
        Assert.Equal(2, result.Scores.Count);
        Assert.Contains(result.Scores, score =>
            score.TrackerSerial == ScriptedCalibrationWizardRuntime.RightTrackerSerial);
    }

    [Fact]
    public void PreservedOppositeTrackerWinningFailsWithoutReassignment()
    {
        var capture = ScriptedCapture(CalibrationWizardHand.Left);

        var result = InternalDriverSingleHandAssociator.Associate(
            capture,
            ScriptedCalibrationWizardRuntime.LeftTrackerSerial);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        Assert.Contains("retained", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LateDisconnectedCorrelatedCandidateRemainsAmbiguityContender()
    {
        var capture = ScriptedCapture(CalibrationWizardHand.Left);
        var winner = capture.TrackerCandidates.Single(candidate =>
            candidate.TrackerSerial == ScriptedCalibrationWizardRuntime.LeftTrackerSerial);
        var ambiguous = new HandMotionCapture(
            capture.Hand,
            capture.ControllerSamples,
            [
                .. capture.TrackerCandidates,
                new TrackerAssociationCandidate(
                    "LHR-LATE-DISCONNECTED",
                    winner.Samples,
                    isConnected: false),
            ]);

        var result = InternalDriverSingleHandAssociator.Associate(
            ambiguous,
            ScriptedCalibrationWizardRuntime.RightTrackerSerial);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        var contender = Assert.Single(result.Scores.Where(score =>
            score.TrackerSerial == "LHR-LATE-DISCONNECTED"));
        Assert.Equal(TrackerAssociationCandidateRejection.Disconnected, contender.Rejection);
        Assert.NotNull(contender.Lag);
    }

    [Fact]
    public void LowValidityCorrelatedCandidateRemainsAmbiguityContender()
    {
        var capture = ScriptedCapture(CalibrationWizardHand.Right);
        var winner = capture.TrackerCandidates.Single(candidate =>
            candidate.TrackerSerial == ScriptedCalibrationWizardRuntime.RightTrackerSerial);
        var lowValiditySamples = winner.Samples.Select((sample, index) =>
            new TimestampedPoseSample(
                sample.MonotonicTimeSeconds,
                sample.Pose,
                index % 4 == 0
                    ? PoseValidity.Position
                    : sample.Validity)).ToArray();
        var ambiguous = new HandMotionCapture(
            capture.Hand,
            capture.ControllerSamples,
            [
                .. capture.TrackerCandidates,
                new TrackerAssociationCandidate(
                    "LHR-LOW-VALIDITY",
                    lowValiditySamples),
            ]);

        var result = InternalDriverSingleHandAssociator.Associate(
            ambiguous,
            ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            new TrackerAssociationOptions
            {
                MinimumOrientationValidityFraction = 0.80d,
            });

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        var contender = Assert.Single(result.Scores.Where(score =>
            score.TrackerSerial == "LHR-LOW-VALIDITY"));
        Assert.Equal(
            TrackerAssociationCandidateRejection.RepeatedlyInvalid,
            contender.Rejection);
        Assert.NotNull(contender.Lag);
    }

    [Fact]
    public void SelectedHandCommitRemovesOnlyPriorActiveKeyAndPreservesOtherRecords()
    {
        WithTemporaryProfilePath(path =>
        {
            var oldLeft = Profile(ControllerHand.Left, "LHR-LEFT-OLD", "old left");
            var right = Profile(ControllerHand.Right, "LHR-RIGHT", "right");
            var unrelated = Profile(ControllerHand.Left, "LHR-FBT", "unrelated");
            CalibrationProfileFile.SaveStore(
                path,
                new CalibrationProfileStore([oldLeft, right, unrelated]));
            var rightBytes = CalibrationProfileJson.SerializeProfile(right);
            var unrelatedBytes = CalibrationProfileJson.SerializeProfile(unrelated);
            var replacement = Profile(
                ControllerHand.Left,
                "LHR-LEFT-NEW",
                "new left");

            ProductionInternalDriverSessionRuntime.RunSelectedHandProfileStoreTransaction(
                path,
                MetaLinkHand.Left,
                stagedPath =>
                {
                    var staged = CalibrationProfileFile.LoadStore(stagedPath);
                    var replaced = InternalDriverCalibration.ReplaceSelectedProfile(
                        staged,
                        replacement,
                        oldLeft.TrackerSerial);
                    CalibrationProfileFile.SaveStore(stagedPath, replaced);
                    return true;
                });

            var committed = CalibrationProfileFile.LoadStore(path);
            Assert.Null(committed.FindCandidateProfile(
                oldLeft.TrackerSerial,
                ControllerHand.Left));
            Assert.Equal(
                replacement,
                committed.FindCandidateProfile(
                    replacement.TrackerSerial,
                    ControllerHand.Left));
            Assert.Equal(
                rightBytes,
                CalibrationProfileJson.SerializeProfile(
                    committed.FindCandidateProfile(
                        right.TrackerSerial,
                        ControllerHand.Right)!));
            Assert.Equal(
                unrelatedBytes,
                CalibrationProfileJson.SerializeProfile(
                    committed.FindCandidateProfile(
                        unrelated.TrackerSerial,
                        ControllerHand.Left)!));
            Assert.Empty(StageFiles(path));
        });
    }

    [Fact]
    public void RemountedSelectedHandRequiresOnlyOneReusableOppositeProfile()
    {
        WithTemporaryProfilePath(path =>
        {
            var oldLeft = Profile(ControllerHand.Left, "LHR-LEFT-OLD", "old left");
            var right = Profile(ControllerHand.Right, "LHR-RIGHT", "right");
            CalibrationProfileFile.SaveStore(
                path,
                new CalibrationProfileStore([oldLeft, right]));
            var runtime = ProductionRuntime(path, new InternalDriverSessionOptions
            {
                Intent = InternalDriverSessionIntent.CalibrateLeft,
            });

            var resolved = runtime.ResolveSelectedHandBase(
                new InternalDriverCalibration(path),
                reusablePair: null,
                InternalDriverCalibrationHandSet.Left,
                ["LHR-LEFT-NEW", "LHR-RIGHT"]);

            Assert.Equal("LHR-RIGHT", resolved.PreservedOpposite.TrackerSerial);
            Assert.Equal("LHR-LEFT-OLD", resolved.ReplacedTrackerSerial);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrIncompatibleOppositeProfileFailsBeforeCapture(bool incompatible)
    {
        WithTemporaryProfilePath(path =>
        {
            var profiles = new List<CalibrationProfile>
            {
                Profile(ControllerHand.Left, "LHR-LEFT-OLD", "old left"),
            };
            if (incompatible)
            {
                profiles.Add(Profile(
                    ControllerHand.Right,
                    "LHR-RIGHT",
                    "incompatible right",
                    controllerModel: "Different Touch"));
            }

            CalibrationProfileFile.SaveStore(path, new CalibrationProfileStore(profiles));
            var runtime = ProductionRuntime(path, new InternalDriverSessionOptions
            {
                Intent = InternalDriverSessionIntent.CalibrateLeft,
            });

            var exception = Assert.Throws<InvalidOperationException>(() =>
                runtime.ResolveSelectedHandBase(
                    new InternalDriverCalibration(path),
                    reusablePair: null,
                    InternalDriverCalibrationHandSet.Left,
                    ["LHR-LEFT-NEW", "LHR-RIGHT"]));

            Assert.Contains("exactly one reusable right", exception.Message);
        });
    }

    [Fact]
    public void ExplicitPriorSelectedKeyDisambiguatesUnrelatedSameHandProfiles()
    {
        WithTemporaryProfilePath(path =>
        {
            CalibrationProfileFile.SaveStore(
                path,
                new CalibrationProfileStore(
                    [
                        Profile(ControllerHand.Left, "LHR-LEFT-OLD", "old left"),
                        Profile(ControllerHand.Left, "LHR-FBT", "unrelated"),
                        Profile(ControllerHand.Right, "LHR-RIGHT", "right"),
                    ]));
            var runtime = ProductionRuntime(path, new InternalDriverSessionOptions
            {
                Intent = InternalDriverSessionIntent.CalibrateLeft,
                PreviousLeftTrackerSerial = "LHR-LEFT-OLD",
            });

            var resolved = runtime.ResolveSelectedHandBase(
                new InternalDriverCalibration(path),
                reusablePair: null,
                InternalDriverCalibrationHandSet.Left,
                ["LHR-LEFT-NEW", "LHR-RIGHT"]);

            Assert.Equal("LHR-LEFT-OLD", resolved.ReplacedTrackerSerial);
        });
    }

    [Fact]
    public void SelectedHandCancellationPreservesExactCanonicalBytesAndLeavesNoResidue()
    {
        WithTemporaryProfilePath(path =>
        {
            var initial = new CalibrationProfileStore(
                [
                    Profile(ControllerHand.Left, "LHR-LEFT", "left"),
                    Profile(ControllerHand.Right, "LHR-RIGHT", "right"),
                    Profile(ControllerHand.Left, "LHR-FBT", "unrelated"),
                ]);
            CalibrationProfileFile.SaveStore(path, initial);
            var canonicalBefore = File.ReadAllBytes(path);
            using var cancellation = new CancellationTokenSource();

            Assert.Throws<OperationCanceledException>(() =>
                ProductionInternalDriverSessionRuntime.RunSelectedHandProfileStoreTransaction(
                    path,
                    MetaLinkHand.Right,
                    stagedPath =>
                    {
                        var staged = CalibrationProfileFile.LoadStore(stagedPath);
                        CalibrationProfileFile.SaveStore(
                            stagedPath,
                            staged.Upsert(Profile(
                                ControllerHand.Right,
                                "LHR-RIGHT",
                                "replacement")));
                        cancellation.Cancel();
                        return true;
                    },
                    cancellation.Token));

            Assert.Equal(canonicalBefore, File.ReadAllBytes(path));
            Assert.Empty(StageFiles(path));
        });
    }

    private static HandMotionCapture ScriptedCapture(CalibrationWizardHand hand)
    {
        var scripted = ScriptedWizardCaptureFactory.Create(hand);
        var controllerSerial = hand == CalibrationWizardHand.Left
            ? ScriptedCalibrationWizardRuntime.LeftControllerSerial
            : ScriptedCalibrationWizardRuntime.RightControllerSerial;
        var controller = scripted.Recording.Streams.Single(stream =>
            stream.Identity.SourceKind == PoseSourceKind.InputController &&
            stream.Identity.DeviceId == controllerSerial);
        var candidates = scripted.Recording.Streams
            .Where(stream => stream.Identity.SourceKind == PoseSourceKind.TrackedPose)
            .Select(stream => new TrackerAssociationCandidate(
                stream.Identity.DeviceId,
                stream.Samples.Select(ToPoseSample).ToArray(),
                stream.Samples.All(sample => sample.IsConnected)))
            .ToArray();
        return new HandMotionCapture(
            hand == CalibrationWizardHand.Left
                ? CalibrationHand.Left
                : CalibrationHand.Right,
            controller.Samples.Select(ToPoseSample).ToArray(),
            candidates);
    }

    private static TimestampedPoseSample ToPoseSample(RecordedPoseSample sample)
    {
        var validity = sample.IsConnected
            ? sample.Validity
            : sample.Validity & ~PoseValidity.TrackingValid;
        return new TimestampedPoseSample(
            sample.MonotonicHostTimeSeconds,
            sample.Pose,
            validity);
    }

    private static CalibrationProfile Profile(
        ControllerHand hand,
        string trackerSerial,
        string profileName,
        string controllerModel = "Quest 2 Touch") => new(
        CalibrationProfileSchema.CurrentVersion,
        profileName,
        hand,
        ControllerRuntimeIdentities.MetaLinkLibOvr,
        controllerModel,
        null,
        trackerSerial,
        CalibrationDriverProfiles.LtbTouch,
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "validated rotation-only",
        new TrackerToControllerTransform(Vector3.Zero, Quaternion.Identity),
        12d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        CreatedUtc);

    private static ProductionInternalDriverSessionRuntime ProductionRuntime(
        string profilePath,
        InternalDriverSessionOptions options) =>
        new(
            options,
            new InternalDriverResolvedPaths(
                Path.Combine(Path.GetDirectoryName(profilePath)!, "settings.json"),
                profilePath,
                Path.Combine(Path.GetDirectoryName(profilePath)!, "driver_ltb"),
                Path.Combine(Path.GetDirectoryName(profilePath)!, "session.jsonl"),
                Path.Combine(Path.GetDirectoryName(profilePath)!, "receipts.json")),
            new NoopDriverLifecycle());

    private static string[] StageFiles(string profilePath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(profilePath)!,
            $".{Path.GetFileName(profilePath)}.*hand-calibration.*",
            SearchOption.TopDirectoryOnly);

    private static void WithTemporaryProfilePath(Action<string> body)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-selected-hand-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            body(Path.Combine(directory, "profiles.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoopDriverLifecycle : ISteamVrDriverLifecycle
    {
        public ValueTask<SteamVrPaths> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SteamVrDriverInspection> InspectAsync(
            string stagedDriverRoot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SteamVrDriverLifecycleResult> RegisterAsync(
            string stagedDriverRoot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SteamVrDriverLifecycleResult> RemoveAsync(
            SteamVrDriverRegistrationReceipt receipt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
