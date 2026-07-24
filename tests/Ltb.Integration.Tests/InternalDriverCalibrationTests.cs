using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Xunit;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverCalibrationTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CapturePreservesCompleteMetaClockAndRawTrackerEvidence()
    {
        var firstMeta = MetaSample(
            MetaLinkHand.Left,
            10.125d,
            RigidTransform.Identity,
            clockUncertaintySeconds: 0.0004d);
        var secondMeta = MetaSample(
            MetaLinkHand.Left,
            10.25d,
            new RigidTransform(
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f),
                new Vector3(0.1f, 0.2f, 0.3f)),
            clockUncertaintySeconds: 0.0009d);
        var firstTracker = TrackerSample(
            10.1d,
            RigidTransform.Identity,
            runtimeTimeSeconds: 455d,
            predictionOffsetSeconds: -0.011d,
            sampleAgeSeconds: 0.003d);
        var disconnectedTracker = new PoseSourceSample(
            new TimestampedPoseSample(
                10.2d,
                RigidTransform.Identity,
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
            isConnected: false,
            PoseTrackingResult.RunningOk,
            runtimeTimeSeconds: 456d,
            predictionOffsetSeconds: -0.011d,
            sampleAgeSeconds: 0.004d);

        var capture = new MetaLinkCalibrationCapture(
            MetaLinkHand.Left,
            "LHR-LEFT",
            [firstMeta, secondMeta],
            [firstTracker, disconnectedTracker]);

        Assert.Equal(MetaLinkHand.Left, capture.Hand);
        Assert.Equal("LHR-LEFT", capture.TrackerSerial);
        Assert.Equal([firstMeta, secondMeta], capture.MetaSamples);
        Assert.Equal([firstTracker, disconnectedTracker], capture.RawTrackerSamples);
        Assert.Equal(10.125d, capture.ControllerPoseSamples[0].MonotonicTimeSeconds);
        Assert.Equal(10.1d, capture.TrackerPoseSamples[0].MonotonicTimeSeconds);
        Assert.True(capture.TrackerPoseSamples[0].IsTrackingValid);
        Assert.False(capture.TrackerPoseSamples[1].IsTrackingValid);
        Assert.Equal(455d, capture.RawTrackerSamples[0].RuntimeTimeSeconds);
        Assert.Equal(-0.011d, capture.RawTrackerSamples[0].PredictionOffsetSeconds);
        Assert.Equal(0.003d, capture.RawTrackerSamples[0].SampleAgeSeconds);
        Assert.Equal(
            new MetaLinkCalibrationClockEvidence(
                firstMeta.Pose.RawMetaTimeSeconds,
                10.125d,
                10_125_000_000L,
                0.0004d),
            capture.ClockEvidence[0]);
        Assert.Equal(0.0009d, capture.MaximumClockUncertaintySeconds);
        Assert.Same(firstMeta, capture.MetaSamples[0]);
    }

    [Fact]
    public void CaptureUsesActualPerChannelMetaTrackingValidity()
    {
        var orientationOnly = MetaSample(
            MetaLinkHand.Right,
            1d,
            RigidTransform.Identity,
            hasValidPosition: false,
            isPositionTracked: false);
        var orientationNotTracked = MetaSample(
            MetaLinkHand.Right,
            2d,
            RigidTransform.Identity,
            isOrientationTracked: false,
            hasValidPosition: true,
            isPositionTracked: true);

        var capture = new MetaLinkCalibrationCapture(
            MetaLinkHand.Right,
            "LHR-RIGHT",
            [orientationOnly, orientationNotTracked],
            []);

        Assert.True(capture.ControllerPoseSamples[0].HasValidOrientation);
        Assert.False(capture.ControllerPoseSamples[0].HasValidPosition);
        Assert.False(capture.ControllerPoseSamples[1].IsTrackingValid);
        Assert.False(capture.ControllerPoseSamples[1].HasValidOrientation);
        Assert.False(capture.ControllerPoseSamples[1].HasValidPosition);
    }

    [Fact]
    public void CaptureRejectsMetaSamplesFromTheOtherHand()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new MetaLinkCalibrationCapture(
                MetaLinkHand.Left,
                "LHR-LEFT",
                [MetaSample(MetaLinkHand.Right, 1d, RigidTransform.Identity)],
                []));

        Assert.Contains("capture hand", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityFreeSchemaTwoProfileRequiresExactFirstPartyMatch()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([Profile()]));
            var calibration = new InternalDriverCalibration(profilePath);

            var lookup = calibration.FindReusableProfile(Context());

            Assert.True(lookup.CanReuse, lookup.Diagnostic);
            Assert.NotNull(lookup.Profile);
            Assert.Equal(CalibrationProfileSchema.CurrentVersion, lookup.Profile.SchemaVersion);
            Assert.Equal(CalibrationDriverProfiles.LtbTouch, lookup.Profile.DriverProfile);
            Assert.Equal(ControllerRuntimeIdentities.MetaLinkLibOvr, lookup.Profile.ControllerRuntime);
            Assert.Null(lookup.Profile.ControllerIdentity);
            Assert.False(lookup.Recalibration!.IsRequired);
            var evidence = ProductionInternalDriverSessionRuntime.ToCalibrationEvidence(
                lookup.Profile);
            Assert.Equal(CalibrationProfileSchema.CurrentVersion, evidence.SchemaVersion);
            Assert.Equal(InternalDriverCalibrationMode.RotationOnly, evidence.SelectedMode);
            Assert.Equal(1d, evidence.Quality.RotationRmsDegrees);
            Assert.Null(evidence.Quality.PositionRmsMillimeters);
            Assert.Null(evidence.Quality.TranslationConditionNumber);
            Assert.Equal(0.95d, evidence.Quality.InlierRatio);
        });
    }

    [Fact]
    public async Task ReusableControllerPairResolvesAsSubsetOfFiveObservedTrackers()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-internal-profile-subset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var profilePath = Path.Combine(directory, "profiles.json");
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [
                    Profile(hand: ControllerHand.Left, trackerSerial: "TRACKER-LEFT"),
                    Profile(hand: ControllerHand.Right, trackerSerial: "TRACKER-RIGHT"),
                ]));
            var paths = new InternalDriverResolvedPaths(
                Path.Combine(directory, "settings.json"),
                profilePath,
                directory,
                Path.Combine(directory, "events.jsonl"),
                Path.Combine(directory, "receipts.json"));
            await using var runtime = new ProductionInternalDriverSessionRuntime(
                new InternalDriverSessionOptions
                {
                    Intent = InternalDriverSessionIntent.NormalStart,
                },
                paths,
                new NoopDriverLifecycle());
            var trackerSerials = new[]
            {
                "FBT-CHEST",
                "FBT-LEFT-FOOT",
                "FBT-RIGHT-FOOT",
                "TRACKER-LEFT",
                "TRACKER-RIGHT",
            };
            var observation = new InternalDriverRuntimeObservation(
                SteamVrRunning: true,
                "SteamVR runtime is running.",
                StoppedMeta(),
                Devices: [],
                trackerSerials.ToDictionary(
                    serial => serial,
                    serial => TrackerSample(
                        10d,
                        new RigidTransform(
                            Quaternion.Identity,
                            new Vector3(serial.Length, 0f, 0f)))));

            var pair = await runtime.ResolveProfilesAsync(
                observation,
                (state, diagnostic, remediation, left, right, progressObservation) => { },
                CancellationToken.None);

            Assert.True(pair.IsValid);
            Assert.Equal("TRACKER-LEFT", pair.Left.TrackerSerial);
            Assert.Equal("TRACKER-RIGHT", pair.Right.TrackerSerial);
            Assert.Equal(InternalDriverProfileReadiness.Reused, pair.Left.Readiness);
            Assert.Equal(InternalDriverProfileReadiness.Reused, pair.Right.Readiness);

            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [
                    Profile(hand: ControllerHand.Left, trackerSerial: "TRACKER-LEFT"),
                    Profile(hand: ControllerHand.Right, trackerSerial: "TRACKER-RIGHT"),
                    Profile(hand: ControllerHand.Right, trackerSerial: "FBT-CHEST"),
                ]));
            var ambiguousError = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runtime.ResolveProfilesAsync(
                    observation,
                    (state, diagnostic, remediation, left, right, progressObservation) => { },
                    CancellationToken.None));
            Assert.Contains(
                "Multiple reusable left/right controller-source profile pairs",
                ambiguousError.Message,
                StringComparison.Ordinal);

            File.Delete(profilePath);
            var progressStates = new List<InternalDriverSessionState>();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await runtime.ResolveProfilesAsync(
                    observation,
                    (state, diagnostic, remediation, left, right, progressObservation) =>
                        progressStates.Add(state),
                    cancelled.Token));
            Assert.Contains(InternalDriverSessionState.Recording, progressStates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitCalibrationDeniesReusableProfilesAndBeginsFreshCaptureFlow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-explicit-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var profilePath = Path.Combine(directory, "profiles.json");
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [
                    Profile(hand: ControllerHand.Left, trackerSerial: "TRACKER-LEFT"),
                    Profile(hand: ControllerHand.Right, trackerSerial: "TRACKER-RIGHT"),
                ]));
            var paths = new InternalDriverResolvedPaths(
                Path.Combine(directory, "settings.json"),
                profilePath,
                directory,
                Path.Combine(directory, "events.jsonl"),
                Path.Combine(directory, "receipts.json"));
            var observation = new InternalDriverRuntimeObservation(
                SteamVrRunning: true,
                "SteamVR runtime is running.",
                StoppedMeta(),
                Devices: [],
                new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
                {
                    ["TRACKER-LEFT"] = TrackerSample(10d, RigidTransform.Identity),
                    ["TRACKER-RIGHT"] = TrackerSample(10d, RigidTransform.Identity),
                });

            await using (var normalRuntime = new ProductionInternalDriverSessionRuntime(
                new InternalDriverSessionOptions
                {
                    Intent = InternalDriverSessionIntent.NormalStart,
                },
                paths,
                new NoopDriverLifecycle()))
            {
                var reused = await normalRuntime.ResolveProfilesAsync(
                    observation,
                    (state, diagnostic, remediation, left, right, progressObservation) => { },
                    CancellationToken.None);

                Assert.Equal(InternalDriverProfileReadiness.Reused, reused.Left.Readiness);
                Assert.Equal(InternalDriverProfileReadiness.Reused, reused.Right.Readiness);
            }

            var progressStates = new List<InternalDriverSessionState>();
            await using var calibrationRuntime = new ProductionInternalDriverSessionRuntime(
                new InternalDriverSessionOptions
                {
                    Intent = InternalDriverSessionIntent.Calibrate,
                },
                paths,
                new NoopDriverLifecycle());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await calibrationRuntime.ResolveProfilesAsync(
                    observation,
                    (state, diagnostic, remediation, left, right, progressObservation) =>
                        progressStates.Add(state),
                    cancelled.Token));

            Assert.Contains(InternalDriverSessionState.Recording, progressStates);
            var retained = CalibrationProfileFile.LoadStore(profilePath);
            Assert.Equal(2, retained.Profiles.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitCalibrationWithFiveCandidatesBeginsFreshCaptureDespiteReusableProfiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-explicit-calibration-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var profilePath = Path.Combine(directory, "profiles.json");
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [
                    Profile(hand: ControllerHand.Left, trackerSerial: "TRACKER-LEFT"),
                    Profile(hand: ControllerHand.Right, trackerSerial: "TRACKER-RIGHT"),
                ]));
            var paths = new InternalDriverResolvedPaths(
                Path.Combine(directory, "settings.json"),
                profilePath,
                directory,
                Path.Combine(directory, "events.jsonl"),
                Path.Combine(directory, "receipts.json"));
            await using var runtime = new ProductionInternalDriverSessionRuntime(
                new InternalDriverSessionOptions
                {
                    Intent = InternalDriverSessionIntent.Calibrate,
                },
                paths,
                new NoopDriverLifecycle());
            var observation = new InternalDriverRuntimeObservation(
                SteamVrRunning: true,
                "SteamVR runtime is running.",
                StoppedMeta(),
                Devices: [],
                new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
                {
                    ["TRACKER-LEFT"] = TrackerSample(10d, RigidTransform.Identity),
                    ["TRACKER-RIGHT"] = TrackerSample(10d, RigidTransform.Identity),
                    ["FBT-CHEST"] = TrackerSample(10d, RigidTransform.Identity),
                    ["FBT-LEFT-FOOT"] = TrackerSample(10d, RigidTransform.Identity),
                    ["FBT-RIGHT-FOOT"] = TrackerSample(10d, RigidTransform.Identity),
                });

            var originalBytes = File.ReadAllBytes(profilePath);
            var progressStates = new List<InternalDriverSessionState>();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await runtime.ResolveProfilesAsync(
                    observation,
                    (state, diagnostic, remediation, left, right, progressObservation) =>
                        progressStates.Add(state),
                    cancelled.Token));

            Assert.Contains(InternalDriverSessionState.Recording, progressStates);
            Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConnectedPhysicalCandidateRosterRetainsTemporarilyInvalidPose()
    {
        var observation = new InternalDriverRuntimeObservation(
            SteamVrRunning: true,
            "SteamVR runtime is running.",
            StoppedMeta(),
            Devices: [],
            new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
            {
                ["TRACKER-RIGHT"] = TrackerSample(10d, RigidTransform.Identity),
                ["DISCONNECTED"] = UnpublishableTrackerSample(
                    10d,
                    isConnected: false,
                    trackingResult: PoseTrackingResult.RunningOk),
                ["TRACKER-LEFT"] = TrackerSample(10d, RigidTransform.Identity),
                ["FBT-CHEST"] = TrackerSample(10d, RigidTransform.Identity),
                ["INVALID-POSE"] = UnpublishableTrackerSample(
                    10d,
                    isConnected: true,
                    trackingResult: PoseTrackingResult.RunningOutOfRange),
            });

        var roster = ProductionInternalDriverSessionRuntime
            .SnapshotConnectedTrackerSerials(observation);
        var expected = new[]
        {
            "FBT-CHEST",
            "INVALID-POSE",
            "TRACKER-LEFT",
            "TRACKER-RIGHT",
        };

        Assert.Equal(expected, roster);
        var left = ProductionInternalDriverSessionRuntime.ToAssociationCapture(
            GuidedCapture(MetaLinkHand.Left, roster));
        var right = ProductionInternalDriverSessionRuntime.ToAssociationCapture(
            GuidedCapture(MetaLinkHand.Right, roster));
        Assert.Equal(expected, left.TrackerCandidates.Select(candidate => candidate.TrackerSerial));
        Assert.Equal(expected, right.TrackerCandidates.Select(candidate => candidate.TrackerSerial));
    }

    [Fact]
    public void AssociationCaptureUsesContinuousConnectionEvidence()
    {
        var roster = new[] { "TRACKER-LEFT", "TRACKER-RIGHT", "FBT-CHEST" };
        var capture = GuidedCapture(
            MetaLinkHand.Left,
            roster,
            discontinuousSerial: "TRACKER-LEFT");

        var associationCapture = ProductionInternalDriverSessionRuntime
            .ToAssociationCapture(capture);

        Assert.False(associationCapture.TrackerCandidates.Single(candidate =>
            candidate.TrackerSerial == "TRACKER-LEFT").IsConnected);
        Assert.True(associationCapture.TrackerCandidates.Single(candidate =>
            candidate.TrackerSerial == "TRACKER-RIGHT").IsConnected);
        Assert.True(associationCapture.TrackerCandidates.Single(candidate =>
            candidate.TrackerSerial == "FBT-CHEST").IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshCalibrationTransactionLeavesPriorPairUntouchedWhenRightValidationFails(
        bool explicitRequest)
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var priorLeft = Profile(
                hand: ControllerHand.Left,
                trackerSerial: "LHR-LEFT",
                profileName: "Prior left");
            var priorRight = Profile(
                hand: ControllerHand.Right,
                trackerSerial: "LHR-RIGHT",
                profileName: "Prior right");
            var unrelated = Profile(
                hand: ControllerHand.Left,
                trackerSerial: "FBT-CHEST",
                profileName: "Unrelated profile");
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([priorLeft, priorRight, unrelated]));
            var originalBytes = File.ReadAllBytes(profilePath);
            var replacementTime = CreatedUtc.AddHours(1);

            var error = Assert.Throws<InvalidOperationException>(() =>
                ProductionInternalDriverSessionRuntime.RunTwoHandProfileStoreTransaction<object>(
                    profilePath,
                    stagedProfilePath =>
                    {
                        var calibration = new InternalDriverCalibration(
                            stagedProfilePath,
                            () => replacementTime);
                        var left = calibration.CalibrateAndSave(
                            new InternalDriverCalibrationContext(
                                MetaLinkHand.Left,
                                "LHR-LEFT",
                                "Quest 2 Touch")
                            {
                                ExplicitRequest = explicitRequest,
                            },
                            SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT"));
                        Assert.True(left.Success, left.Diagnostic);
                        Assert.Equal(
                            replacementTime,
                            CalibrationProfileFile.LoadStore(stagedProfilePath)
                                .FindCandidateProfile("LHR-LEFT", ControllerHand.Left)!
                                .CreatedUtc);

                        var right = calibration.CalibrateAndSave(
                            new InternalDriverCalibrationContext(
                                MetaLinkHand.Right,
                                "LHR-RIGHT",
                                "Quest 2 Touch")
                            {
                                ExplicitRequest = explicitRequest,
                            },
                            RejectedCapture(MetaLinkHand.Right, "LHR-RIGHT"));
                        Assert.False(right.Success);
                        throw new InvalidOperationException(right.Diagnostic);
                    }));

            Assert.Contains("Right calibration rejected", error.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
            var retained = CalibrationProfileFile.LoadStore(profilePath);
            Assert.Equal("Prior left", retained.FindCandidateProfile(
                "LHR-LEFT",
                ControllerHand.Left)!.ProfileName);
            Assert.Equal("Prior right", retained.FindCandidateProfile(
                "LHR-RIGHT",
                ControllerHand.Right)!.ProfileName);
            Assert.Equal("Unrelated profile", retained.FindCandidateProfile(
                "FBT-CHEST",
                ControllerHand.Left)!.ProfileName);
            Assert.Empty(TwoHandCalibrationStageFiles(profilePath));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshCalibrationTransactionCommitsBothProfilesAndPreservesUnrelatedEntries(
        bool explicitRequest)
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var priorLeft = Profile(
                hand: ControllerHand.Left,
                trackerSerial: "LHR-LEFT",
                profileName: "Prior left");
            var priorRight = Profile(
                hand: ControllerHand.Right,
                trackerSerial: "LHR-RIGHT",
                profileName: "Prior right");
            var unrelated = Profile(
                hand: ControllerHand.Left,
                trackerSerial: "FBT-CHEST",
                profileName: "Unrelated profile");
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([priorLeft, priorRight, unrelated]));
            var replacementTime = CreatedUtc.AddHours(1);

            var result = ProductionInternalDriverSessionRuntime
                .RunTwoHandProfileStoreTransaction(
                    profilePath,
                    stagedProfilePath =>
                    {
                        var calibration = new InternalDriverCalibration(
                            stagedProfilePath,
                            () => replacementTime);
                        var left = calibration.CalibrateAndSave(
                            new InternalDriverCalibrationContext(
                                MetaLinkHand.Left,
                                "LHR-LEFT",
                                "Quest 2 Touch")
                            {
                                ExplicitRequest = explicitRequest,
                            },
                            SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT"));
                        Assert.True(left.Success, left.Diagnostic);
                        var right = calibration.CalibrateAndSave(
                            new InternalDriverCalibrationContext(
                                MetaLinkHand.Right,
                                "LHR-RIGHT",
                                "Quest 2 Touch")
                            {
                                ExplicitRequest = explicitRequest,
                            },
                            SyntheticCapture(MetaLinkHand.Right, "LHR-RIGHT"));
                        Assert.True(right.Success, right.Diagnostic);
                        return "committed";
                    });

            Assert.Equal("committed", result);
            var committed = CalibrationProfileFile.LoadStore(profilePath);
            Assert.Equal(replacementTime, committed.FindCandidateProfile(
                "LHR-LEFT",
                ControllerHand.Left)!.CreatedUtc);
            Assert.Equal(replacementTime, committed.FindCandidateProfile(
                "LHR-RIGHT",
                ControllerHand.Right)!.CreatedUtc);
            Assert.Equal("Unrelated profile", committed.FindCandidateProfile(
                "FBT-CHEST",
                ControllerHand.Left)!.ProfileName);
            Assert.Empty(TwoHandCalibrationStageFiles(profilePath));
        });
    }

    [Fact]
    public void CancellationAfterStagedCalibrationPreservesCanonicalBytesAndRemovesStage()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var prior = new CalibrationProfileStore(
            [Profile(
                hand: ControllerHand.Left,
                trackerSerial: "LHR-LEFT",
                profileName: "Prior left")]);
            CalibrationProfileFile.SaveStore(profilePath, prior);
            var originalBytes = File.ReadAllBytes(profilePath);
            using var cancellation = new CancellationTokenSource();

            Assert.Throws<OperationCanceledException>(() =>
                ProductionInternalDriverSessionRuntime.RunTwoHandProfileStoreTransaction(
                    profilePath,
                    stagedProfilePath =>
                    {
                        CalibrationProfileFile.SaveStore(
                            stagedProfilePath,
                            new CalibrationProfileStore(
                            [
                                Profile(
                                    hand: ControllerHand.Left,
                                    trackerSerial: "LHR-LEFT",
                                    profileName: "Replacement left"),
                            ]));
                        cancellation.Cancel();
                        return "must not commit";
                    },
                    cancellation.Token));

            Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
            Assert.Empty(TwoHandCalibrationStageFiles(profilePath));
        });
    }

    [Theory]
    [InlineData("different-runtime", "Quest 2 Touch", null)]
    [InlineData("meta_link_libovr", "Different Touch Model", null)]
    [InlineData("meta_link_libovr", "Quest 2 Touch", "invented-identity")]
    public void RuntimeModelOrUnexpectedIdentityMismatchRequiresCalibration(
        string storedRuntime,
        string storedModel,
        string? storedIdentity)
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [Profile(
                    controllerRuntime: storedRuntime,
                    controllerModel: storedModel,
                    controllerIdentity: storedIdentity)]));
            var calibration = new InternalDriverCalibration(profilePath);

            var lookup = calibration.FindReusableProfile(Context());

            Assert.False(lookup.CanReuse);
            Assert.Null(lookup.Profile);
            Assert.Contains(
                RecalibrationTriggerKind.ControllerIdentityChanged,
                lookup.Recalibration!.Triggers.Select(trigger => trigger.Kind));
        });
    }

    [Fact]
    public void LegacyProfileAndWrongHandOrTrackerNeverAuthorizeReuse()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([LegacyProfile()]));
            var calibration = new InternalDriverCalibration(profilePath);

            var legacy = calibration.FindReusableProfile(Context());
            var wrongTracker = calibration.FindReusableProfile(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Left,
                    "LHR-OTHER",
                    "Quest 2 Touch"));
            var wrongHand = calibration.FindReusableProfile(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Right,
                    "LHR-LEFT",
                    "Quest 2 Touch"));

            Assert.False(legacy.CanReuse);
            Assert.Contains(
                RecalibrationTriggerKind.SchemaVersionChanged,
                legacy.Recalibration!.Triggers.Select(trigger => trigger.Kind));
            Assert.False(wrongTracker.CanReuse);
            Assert.Null(wrongTracker.Recalibration);
            Assert.False(wrongHand.CanReuse);
            Assert.Null(wrongHand.Recalibration);
        });
    }

    [Fact]
    public void ExplicitMountOrValidationTriggerRequiresCalibration()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([Profile()]));
            var calibration = new InternalDriverCalibration(profilePath);

            var lookup = calibration.FindReusableProfile(Context() with
            {
                ExplicitRequest = true,
                MountMoved = true,
                ValidationThresholdExceeded = true,
            });

            Assert.False(lookup.CanReuse);
            Assert.Equal(
                [
                    RecalibrationTriggerKind.ExplicitRequest,
                    RecalibrationTriggerKind.MountMoved,
                    RecalibrationTriggerKind.ValidationThresholdExceeded,
                ],
                lookup.Recalibration!.Triggers.Select(trigger => trigger.Kind));
        });
    }

    [Fact]
    public void ExistingPipelineCalibratesAndPersistsIdentityFreeSchemaTwoProfile()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var capture = SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT");
            var calibration = new InternalDriverCalibration(
                profilePath,
                () => CreatedUtc);

            var result = calibration.CalibrateAndSave(Context(), capture);

            Assert.True(result.Success, result.Diagnostic);
            Assert.True(result.PipelineResult.Success, result.PipelineResult.Reason);
            Assert.Equal(CalibrationModel.FullSixDof, result.PipelineResult.SelectedModel);
            Assert.Equal(capture.ClockEvidence, result.ClockEvidence);
            Assert.Equal(360, result.ClockEvidence.Count);
            Assert.Equal(0.000859d, result.Capture.MaximumClockUncertaintySeconds, 12);

            var profile = CalibrationProfileFile.LoadStore(profilePath)
                .FindMatchingProfile(
                    "LHR-LEFT",
                    ControllerHand.Left,
                    CalibrationDriverProfiles.LtbTouch,
                    ControllerRuntimeIdentities.MetaLinkLibOvr,
                    "Quest 2 Touch",
                    controllerIdentity: null);
            Assert.NotNull(profile);
            Assert.Null(profile.ControllerIdentity);
            Assert.Equal(CalibrationProfileSchema.CurrentVersion, profile.SchemaVersion);
            Assert.Equal(ProfileCalibrationMode.FullSixDof, profile.SelectedMode);
            Assert.Equal(CreatedUtc, profile.CreatedUtc);
            Assert.InRange(profile.EstimatedLagMilliseconds, 10d, 14d);

            var evidence = ProductionInternalDriverSessionRuntime.ToCalibrationEvidence(profile);
            Assert.Equal(InternalDriverCalibrationMode.FullSixDof, evidence.SelectedMode);
            Assert.Equal(profile.SelectionReason, evidence.SelectionReason);
            Assert.Equal(profile.EstimatedLagMilliseconds, evidence.EstimatedLagMilliseconds);
            Assert.Equal(profile.Quality.RotationRmsDegrees, evidence.Quality.RotationRmsDegrees);
            Assert.Equal(profile.Quality.PositionRmsMillimeters, evidence.Quality.PositionRmsMillimeters);
            Assert.Equal(profile.Quality.TranslationCondition, evidence.Quality.TranslationConditionNumber);
            Assert.Equal(profile.Quality.InlierRatio, evidence.Quality.InlierRatio);
            Assert.Equal(CreatedUtc, evidence.CreatedUtc);

            var later = calibration.FindReusableProfile(Context());
            Assert.True(later.CanReuse, later.Diagnostic);
        });
    }

    [Fact]
    public void SuccessfulRecalibrationReplacesIncompatibleSameHandProfile()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore(
                [Profile(controllerModel: "Previous Touch Model")]));
            var calibration = new InternalDriverCalibration(
                profilePath,
                () => CreatedUtc);
            Assert.False(calibration.FindReusableProfile(Context()).CanReuse);

            var result = calibration.CalibrateAndSave(
                Context(),
                SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT"));

            Assert.True(result.Success, result.Diagnostic);
            var stored = Assert.Single(CalibrationProfileFile.LoadStore(profilePath).Profiles);
            Assert.Equal("Quest 2 Touch", stored.ControllerModel);
            Assert.Null(stored.ControllerIdentity);
        });
    }

    [Fact]
    public void SuccessfulCalibrationNeverOverwritesAnUnknownFutureSchema()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            const string unknownStore = """
                {
                  "profiles": [
                    {
                      "schema_version": 99,
                      "profile_name": "Future profile must survive",
                      "hand": "left",
                      "controller_runtime": "future_runtime",
                      "controller_model": "Future Touch",
                      "tracker_serial": "LHR-FUTURE",
                      "calibration_policy": "auto",
                      "selected_mode": "rotation_only",
                      "selection_reason": "future schema fixture",
                      "tracker_to_controller": {
                        "translation_m": [0, 0, 0],
                        "rotation_xyzw": [0, 0, 0, 1]
                      },
                      "estimated_lag_ms": 0,
                      "quality": {
                        "rotation_rms_deg": 0,
                        "position_rms_mm": null,
                        "translation_condition": null,
                        "inlier_ratio": 1
                      },
                      "created_utc": "2026-07-21T00:00:00Z"
                    }
                  ]
                }
                """;
            File.WriteAllText(profilePath, unknownStore);
            var calibration = new InternalDriverCalibration(
                profilePath,
                () => CreatedUtc);

            var lookup = calibration.FindReusableProfile(Context());
            var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
                calibration.CalibrateAndSave(
                    Context(),
                    SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT")));

            Assert.False(lookup.CanReuse);
            Assert.Contains("will not be overwritten", lookup.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(
                CalibrationProfileFormatReason.UnsupportedSchemaVersion,
                exception.Reason);
            Assert.Equal(unknownStore, File.ReadAllText(profilePath));
        });
    }

    [Fact]
    public void SuccessfulCalibrationMayReplaceTheExactLegacySchemaOneEntry()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            CalibrationProfileFile.SaveStore(
                profilePath,
                new CalibrationProfileStore([LegacyProfile()]));
            var calibration = new InternalDriverCalibration(
                profilePath,
                () => CreatedUtc);

            var result = calibration.CalibrateAndSave(
                Context(),
                SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT"));

            Assert.True(result.Success, result.Diagnostic);
            var stored = Assert.Single(CalibrationProfileFile.LoadStore(profilePath).Profiles);
            Assert.Equal(CalibrationProfileSchema.CurrentVersion, stored.SchemaVersion);
            Assert.Equal(ControllerRuntimeIdentities.MetaLinkLibOvr, stored.ControllerRuntime);
            Assert.Equal(CalibrationDriverProfiles.LtbTouch, stored.DriverProfile);
            Assert.Null(stored.ControllerIdentity);
        });
    }

    [Fact]
    public void RejectedCaptureDoesNotCreateOrOverwriteAProfile()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var staticMeta = Enumerable.Range(0, 40)
                .Select(index => MetaSample(
                    MetaLinkHand.Left,
                    1d + (index / 90d),
                    RigidTransform.Identity))
                .ToArray();
            var staticTrackers = Enumerable.Range(0, 40)
                .Select(index => TrackerSample(
                    1d + (index / 90d),
                    RigidTransform.Identity))
                .ToArray();
            var calibration = new InternalDriverCalibration(profilePath);

            var result = calibration.CalibrateAndSave(
                Context(),
                new MetaLinkCalibrationCapture(
                    MetaLinkHand.Left,
                    "LHR-LEFT",
                    staticMeta,
                    staticTrackers));

            Assert.False(result.Success);
            Assert.Equal(HandCalibrationFailure.TimeAlignmentRejected, result.PipelineResult.Failure);
            Assert.False(File.Exists(profilePath));
        });
    }

    [Fact]
    public void CaptureAndContextMustUseTheSameExactHandAndTracker()
    {
        WithTemporaryProfilePath(profilePath =>
        {
            var calibration = new InternalDriverCalibration(profilePath);
            var capture = SyntheticCapture(MetaLinkHand.Left, "LHR-LEFT");

            Assert.Throws<ArgumentException>(() => calibration.CalibrateAndSave(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Right,
                    "LHR-LEFT",
                    "Quest 2 Touch"),
                capture));
            Assert.Throws<ArgumentException>(() => calibration.CalibrateAndSave(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Left,
                    "LHR-OTHER",
                    "Quest 2 Touch"),
                capture));
        });
    }

    private static MetaLinkCalibrationCapture SyntheticCapture(
        MetaLinkHand hand,
        string trackerSerial)
    {
        const int sampleCount = 360;
        const double sampleRateHz = 90d;
        const double controllerLagSeconds = 0.012d;
        var questFromDriver = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.42f, -0.31f, 0.19f),
            new Vector3(0.7f, -0.2f, 1.1f));
        var mount = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.25f, -0.18f, 0.12f),
            new Vector3(0.014f, -0.052f, 0.031f));
        var meta = new MetaLinkControllerSnapshot[sampleCount];
        var trackers = new PoseSourceSample[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var relativeTime = index / sampleRateHz;
            var trackerTime = 1d + relativeTime;
            var trackerPose = TrackerPose(relativeTime);
            trackers[index] = TrackerSample(
                trackerTime,
                trackerPose,
                runtimeTimeSeconds: 500d + relativeTime,
                predictionOffsetSeconds: 0d,
                sampleAgeSeconds: 0.002d);
            meta[index] = MetaSample(
                hand,
                trackerTime + controllerLagSeconds,
                questFromDriver * trackerPose * mount,
                clockUncertaintySeconds: 0.0005d + (index * 0.000001d));
        }

        return new MetaLinkCalibrationCapture(hand, trackerSerial, meta, trackers);
    }

    private static MetaLinkCalibrationCapture RejectedCapture(
        MetaLinkHand hand,
        string trackerSerial)
    {
        var meta = Enumerable.Range(0, 40)
            .Select(index => MetaSample(
                hand,
                1d + (index / 90d),
                RigidTransform.Identity))
            .ToArray();
        var trackers = Enumerable.Range(0, 40)
            .Select(index => TrackerSample(
                1d + (index / 90d),
                RigidTransform.Identity))
            .ToArray();
        return new MetaLinkCalibrationCapture(hand, trackerSerial, meta, trackers);
    }

    private static RigidTransform TrackerPose(double time)
    {
        var seconds = (float)time;
        var yaw = 0.85f * MathF.Sin(2f * MathF.PI * 0.43f * seconds);
        var pitch = 0.68f * MathF.Sin((2f * MathF.PI * 0.71f * seconds) + 0.31f);
        var roll = 0.78f * MathF.Sin((2f * MathF.PI * 0.89f * seconds) + 0.83f);
        return new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll),
            new Vector3(
                0.23f * MathF.Sin(2f * MathF.PI * 0.29f * seconds),
                0.18f * MathF.Cos(2f * MathF.PI * 0.37f * seconds),
                1.15f + (0.13f * MathF.Sin(2f * MathF.PI * 0.23f * seconds))));
    }

    private static MetaLinkControllerSnapshot MetaSample(
        MetaLinkHand hand,
        double mappedTimeSeconds,
        RigidTransform pose,
        double clockUncertaintySeconds = 0.0005d,
        bool isOrientationTracked = true,
        bool hasValidPosition = true,
        bool isPositionTracked = true) => new(
        hand,
        new MetaLinkPoseSnapshot(
            pose,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            isOrientationTracked,
            isPositionTracked,
            hasValidOrientation: true,
            hasValidPosition,
            rawMetaTimeSeconds: mappedTimeSeconds - 0.25d,
            appMonotonicTimeSeconds: mappedTimeSeconds,
            appMonotonicTimeNanoseconds: checked((long)Math.Round(
                mappedTimeSeconds * 1_000_000_000d,
                MidpointRounding.AwayFromZero)),
            clockUncertaintySeconds),
        default,
        default,
        default,
        MetaLinkBatteryState.Unavailable);

    private static PoseSourceSample TrackerSample(
        double timeSeconds,
        RigidTransform pose,
        double? runtimeTimeSeconds = null,
        double? predictionOffsetSeconds = null,
        double? sampleAgeSeconds = null) => new(
        new TimestampedPoseSample(
            timeSeconds,
            pose,
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
        isConnected: true,
        PoseTrackingResult.RunningOk,
        runtimeTimeSeconds,
        predictionOffsetSeconds,
        sampleAgeSeconds,
        linearVelocityMetersPerSecond: new Vector3(0.1f, 0.2f, 0.3f),
        angularVelocityRadiansPerSecond: new Vector3(0.4f, 0.5f, 0.6f));

    private static PoseSourceSample UnpublishableTrackerSample(
        double timeSeconds,
        bool isConnected,
        PoseTrackingResult trackingResult) => new(
        new TimestampedPoseSample(
            timeSeconds,
            RigidTransform.Identity,
            PoseValidity.Orientation | PoseValidity.Position),
        isConnected,
        trackingResult);

    private static ProductionInternalDriverSessionRuntime.GuidedHandCapture GuidedCapture(
        MetaLinkHand hand,
        IReadOnlyList<string> trackerSerials,
        string? discontinuousSerial = null)
    {
        var meta = new[]
        {
            MetaSample(
                hand,
                mappedTimeSeconds: 10d,
                RigidTransform.Identity),
        };
        var trackers = trackerSerials.ToDictionary(
            serial => serial,
            serial => new List<PoseSourceSample>
            {
                TrackerSample(10d, RigidTransform.Identity),
            },
            StringComparer.Ordinal);
        var continuity = trackerSerials.ToDictionary(
            serial => serial,
            serial => !string.Equals(serial, discontinuousSerial, StringComparison.Ordinal),
            StringComparer.Ordinal);
        return new ProductionInternalDriverSessionRuntime.GuidedHandCapture(
            hand,
            meta,
            trackers,
            continuity);
    }

    private static InternalDriverCalibrationContext Context() => new(
        MetaLinkHand.Left,
        "LHR-LEFT",
        "Quest 2 Touch");

    private static CalibrationProfile Profile(
        string controllerRuntime = ControllerRuntimeIdentities.MetaLinkLibOvr,
        string controllerModel = "Quest 2 Touch",
        string? controllerIdentity = null,
        ControllerHand hand = ControllerHand.Left,
        string trackerSerial = "LHR-LEFT",
        string? profileName = null,
        DateTimeOffset? createdUtc = null) => new(
        CalibrationProfileSchema.CurrentVersion,
        profileName ?? $"Synthetic internal-driver {hand} profile",
        hand,
        controllerRuntime,
        controllerModel,
        controllerIdentity,
        trackerSerial,
        CalibrationDriverProfiles.LtbTouch,
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "translation unobservable; rotation-only fallback",
        new TrackerToControllerTransform(Vector3.Zero, Quaternion.Identity),
        12d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        createdUtc ?? CreatedUtc);

    private static string[] TwoHandCalibrationStageFiles(string profilePath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(profilePath)!,
            $".{Path.GetFileName(profilePath)}.two-hand-calibration.*",
            SearchOption.TopDirectoryOnly);

    private static MetaLinkRuntimeSnapshot StoppedMeta() => new(
        0,
        0d,
        new MetaLinkHandSnapshot(
            MetaLinkHand.Left,
            MetaLinkReadiness.RuntimeStopped,
            "not used by profile subset resolution"),
        new MetaLinkHandSnapshot(
            MetaLinkHand.Right,
            MetaLinkReadiness.RuntimeStopped,
            "not used by profile subset resolution"));

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

    private static CalibrationProfile LegacyProfile() => new(
        CalibrationProfileSchema.LegacyVersion,
        "Legacy ALVR profile",
        ControllerHand.Left,
        ControllerRuntimeIdentities.LegacyAlvr,
        "Quest 2 Touch",
        controllerSerial: null,
        "LHR-LEFT",
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "legacy rotation-only",
        new TrackerToControllerTransform(Vector3.Zero, Quaternion.Identity),
        12d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        CreatedUtc);

    private static void WithTemporaryProfilePath(Action<string> body)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-internal-calibration-{Guid.NewGuid():N}");
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
}
