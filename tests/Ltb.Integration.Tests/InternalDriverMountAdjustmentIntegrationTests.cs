using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverMountAdjustmentIntegrationTests
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task SchemaTwoReuseStaysByteExactUntilExplicitSaveUpgradesThePair()
    {
        using var fixture = new SteamVrSettingsFixture();
        var left = Profile(
            CalibrationProfileSchema.DriverProfileVersion,
            ControllerHand.Left,
            "TRACKER-LEFT",
            "left schema two");
        var right = Profile(
            CalibrationProfileSchema.DriverProfileVersion,
            ControllerHand.Right,
            "TRACKER-RIGHT",
            "right schema two");
        var unrelated = Profile(
            CalibrationProfileSchema.CurrentVersion,
            ControllerHand.Left,
            "TRACKER-UNRELATED",
            "unrelated schema three");
        CalibrationProfileFile.SaveStore(
            fixture.ProfilePath,
            new CalibrationProfileStore([left, right, unrelated]));
        var bytesBeforeReuse = File.ReadAllBytes(fixture.ProfilePath);
        var unrelatedBytes = CalibrationProfileJson.SerializeProfile(unrelated);
        await using var runtime = fixture.CreateRuntime();
        var progress = new List<InternalDriverSessionState>();

        var resolved = await runtime.ResolveProfilesAsync(
            ReuseObservation(),
            (state, _, _, _, _, _) => progress.Add(state),
            CancellationToken.None);

        Assert.Empty(progress);
        Assert.Equal(
            CalibrationProfileSchema.DriverProfileVersion,
            resolved.Left.Calibration!.SchemaVersion);
        Assert.Equal(
            CalibrationProfileSchema.DriverProfileVersion,
            resolved.Right.Calibration!.SchemaVersion);
        Assert.Equal(MountAdjustment.Identity, resolved.Left.MountAdjustment);
        Assert.Equal(MountAdjustment.Identity, resolved.Right.MountAdjustment);
        Assert.Equal(
            resolved.Left.TrackerFromController,
            resolved.Left.EffectiveTrackerFromController);
        Assert.Equal(
            resolved.Right.TrackerFromController,
            resolved.Right.EffectiveTrackerFromController);
        Assert.Equal(bytesBeforeReuse, File.ReadAllBytes(fixture.ProfilePath));

        var leftAdjustment = new MountAdjustment(
            new RigidTransform(
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f),
                new Vector3(0.015f, 0f, 0f)),
            new RigidTransform(
                Quaternion.Identity,
                new Vector3(0f, 0.02f, 0f)));
        var persisted = runtime.SaveMountAdjustments(
            resolved,
            leftAdjustment,
            MountAdjustment.Identity);

        Assert.Equal(CalibrationProfileSchema.CurrentVersion, persisted.Left.SourceProfile!.SchemaVersion);
        Assert.Equal(CalibrationProfileSchema.CurrentVersion, persisted.Right.SourceProfile!.SchemaVersion);
        Assert.Equal(leftAdjustment, persisted.Left.MountAdjustment);
        Assert.Equal(
            leftAdjustment.ApplyTo(persisted.Left.TrackerFromController),
            persisted.Left.EffectiveTrackerFromController);
        var reloaded = CalibrationProfileFile.LoadStore(fixture.ProfilePath);
        Assert.Equal(
            leftAdjustment,
            reloaded.FindCandidateProfile("TRACKER-LEFT", ControllerHand.Left)!
                .MountAdjustment);
        Assert.Equal(
            MountAdjustment.Identity,
            reloaded.FindCandidateProfile("TRACKER-RIGHT", ControllerHand.Right)!
                .MountAdjustment);
        Assert.Equal(
            unrelatedBytes,
            CalibrationProfileJson.SerializeProfile(
                reloaded.FindCandidateProfile(
                    "TRACKER-UNRELATED",
                    ControllerHand.Left)!));
    }

    [Fact]
    public async Task ExplicitSaveRefusesChangedSourceWithoutOverwritingExternalBytes()
    {
        using var fixture = new SteamVrSettingsFixture();
        var left = Profile(
            CalibrationProfileSchema.DriverProfileVersion,
            ControllerHand.Left,
            "TRACKER-LEFT",
            "left schema two");
        var right = Profile(
            CalibrationProfileSchema.DriverProfileVersion,
            ControllerHand.Right,
            "TRACKER-RIGHT",
            "right schema two");
        CalibrationProfileFile.SaveStore(
            fixture.ProfilePath,
            new CalibrationProfileStore([left, right]));
        await using var runtime = fixture.CreateRuntime();
        var resolved = await runtime.ResolveProfilesAsync(
            ReuseObservation(),
            (_, _, _, _, _, _) => { },
            CancellationToken.None);
        var externalLeft = Profile(
            CalibrationProfileSchema.DriverProfileVersion,
            ControllerHand.Left,
            "TRACKER-LEFT",
            "external writer");
        CalibrationProfileFile.SaveStore(
            fixture.ProfilePath,
            new CalibrationProfileStore([externalLeft, right]));
        var externalBytes = File.ReadAllBytes(fixture.ProfilePath);

        var exception = Assert.Throws<IOException>(() =>
            runtime.SaveMountAdjustments(
                resolved,
                MountAdjustment.Identity,
                MountAdjustment.Identity));

        Assert.Contains("changed after it was loaded", exception.Message);
        Assert.Equal(externalBytes, File.ReadAllBytes(fixture.ProfilePath));
    }

    [Fact]
    public async Task ProductionBackendNeutralizesExactPathsAndRestoresOwnedSnapshot()
    {
        using var fixture = new SteamVrSettingsFixture();
        var originalBytes = File.ReadAllBytes(fixture.SettingsPath);
        using var backend = fixture.CreateBackend();

        var receipt = await backend.CaptureAndNeutralizeAsync(
            TrackerPaths(),
            CancellationToken.None);

        AssertTrackerRoles(
            fixture.SettingsPath,
            leftRole: "TrackerRole_None",
            rightRole: "TrackerRole_None",
            unrelatedRole: "TrackerRole_Waist");
        Assert.True(File.Exists(fixture.ReceiptPath));

        await backend.RestoreAsync(receipt, CancellationToken.None);

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task LaterStartupRecoversOnlyTheTransactionOwnedBackup()
    {
        using var fixture = new SteamVrSettingsFixture();
        var originalBytes = File.ReadAllBytes(fixture.SettingsPath);
        using (var interrupted = fixture.CreateBackend())
        {
            _ = await interrupted.CaptureAndNeutralizeAsync(
                TrackerPaths(),
                CancellationToken.None);
        }

        using var recovery = fixture.CreateBackend();
        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.True(result.Restored, result.Diagnostic);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task LaterStartupRefusesExternalWriterAndRetainsRecoveryReceipt()
    {
        using var fixture = new SteamVrSettingsFixture();
        using (var interrupted = fixture.CreateBackend())
        {
            _ = await interrupted.CaptureAndNeutralizeAsync(
                TrackerPaths(),
                CancellationToken.None);
        }

        var externalPostImage = SettingsJson(
            leftRole: "TrackerRole_LeftFoot",
            rightRole: "TrackerRole_None",
            unrelatedRole: "TrackerRole_ExternalWriter");
        File.WriteAllText(fixture.SettingsPath, externalPostImage);

        using var recovery = fixture.CreateBackend();
        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.False(result.Restored);
        Assert.Contains("post-image", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(externalPostImage, File.ReadAllText(fixture.SettingsPath));
        Assert.True(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task AlreadyRestoredSettingsClearReceiptAfterRestoreDeleteCrashWindow()
    {
        using var fixture = new SteamVrSettingsFixture();
        byte[] retainedReceipt;
        InternalDriverTrackerNeutralizationReceipt receipt;
        using (var interrupted = fixture.CreateBackend())
        {
            receipt = await interrupted.CaptureAndNeutralizeAsync(
                TrackerPaths(),
                CancellationToken.None);
            retainedReceipt = File.ReadAllBytes(fixture.ReceiptPath);
            await interrupted.RestoreAsync(receipt, CancellationToken.None);
        }

        File.WriteAllBytes(fixture.ReceiptPath, retainedReceipt);
        using var recovery = fixture.CreateBackend();
        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.True(result.Restored, result.Diagnostic);
        Assert.Contains("already match", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.ReceiptPath));
        AssertTrackerRoles(
            fixture.SettingsPath,
            leftRole: "TrackerRole_LeftFoot",
            rightRole: "TrackerRole_RightFoot",
            unrelatedRole: "TrackerRole_Waist");
    }

    private static IReadOnlyList<InternalDriverTrackerPath> TrackerPaths() =>
        [
            new(
                ProtocolHand.Left,
                "TRACKER-LEFT",
                "/devices/lighthouse/TRACKER-LEFT"),
            new(
                ProtocolHand.Right,
                "TRACKER-RIGHT",
                "/devices/lighthouse/TRACKER-RIGHT"),
        ];

    private static InternalDriverRuntimeObservation ReuseObservation()
    {
        var sample = new PoseSourceSample(
            new TimestampedPoseSample(
                10d,
                RigidTransform.Identity,
                PoseValidity.Orientation |
                    PoseValidity.Position |
                    PoseValidity.TrackingValid),
            isConnected: true,
            PoseTrackingResult.RunningOk);
        return new InternalDriverRuntimeObservation(
            SteamVrRunning: true,
            "SteamVR ready",
            new MetaLinkRuntimeSnapshot(
                1,
                10d,
                new MetaLinkHandSnapshot(
                    MetaLinkHand.Left,
                    MetaLinkReadiness.RuntimeStopped,
                    "left not used during profile reuse"),
                new MetaLinkHandSnapshot(
                    MetaLinkHand.Right,
                    MetaLinkReadiness.RuntimeStopped,
                    "right not used during profile reuse")),
            Array.Empty<SteamVrDeviceDescriptor>(),
            new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
            {
                ["TRACKER-LEFT"] = sample,
                ["TRACKER-RIGHT"] = sample,
            });
    }

    private static CalibrationProfile Profile(
        int schemaVersion,
        ControllerHand hand,
        string trackerSerial,
        string name) => new(
        schemaVersion,
        name,
        hand,
        ControllerRuntimeIdentities.MetaLinkLibOvr,
        "Quest 2 Touch",
        controllerIdentity: null,
        trackerSerial,
        CalibrationDriverProfiles.LtbTouch,
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.FullSixDof,
        "validated full 6DoF",
        new TrackerToControllerTransform(
            hand == ControllerHand.Left
                ? new Vector3(0.1f, 0f, 0f)
                : new Vector3(0f, 0.1f, 0f),
            Quaternion.Identity),
        12d,
        new CalibrationProfileQuality(1d, 2d, 3d, 0.95d),
        new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero));

    private static string SettingsJson(
        string leftRole = "TrackerRole_LeftFoot",
        string rightRole = "TrackerRole_RightFoot",
        string unrelatedRole = "TrackerRole_Waist")
    {
        var root = new JsonObject
        {
            ["steamvr"] = new JsonObject
            {
                ["activateMultipleDrivers"] = true,
            },
            ["trackers"] = new JsonObject
            {
                ["/devices/lighthouse/TRACKER-LEFT"] = leftRole,
                ["/devices/lighthouse/TRACKER-RIGHT"] = rightRole,
                ["/devices/lighthouse/UNRELATED"] = unrelatedRole,
            },
        };
        return root.ToJsonString(SettingsJsonOptions) + "\n";
    }

    private static void AssertTrackerRoles(
        string settingsPath,
        string leftRole,
        string rightRole,
        string unrelatedRole)
    {
        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var trackers = root["trackers"]!.AsObject();
        Assert.Equal(leftRole, trackers["/devices/lighthouse/TRACKER-LEFT"]!.GetValue<string>());
        Assert.Equal(rightRole, trackers["/devices/lighthouse/TRACKER-RIGHT"]!.GetValue<string>());
        Assert.Equal(unrelatedRole, trackers["/devices/lighthouse/UNRELATED"]!.GetValue<string>());
    }

    private sealed class SteamVrSettingsFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"ltb-role-neutralization-{Guid.NewGuid():N}");

        public SteamVrSettingsFixture()
        {
            Directory.CreateDirectory(_root);
            SettingsPath = Path.Combine(_root, "steamvr.vrsettings");
            ReceiptPath = Path.Combine(_root, "tracker-role-recovery.json");
            ProfilePath = Path.Combine(_root, "calibration-profiles.json");
            File.WriteAllText(SettingsPath, SettingsJson());
        }

        public string SettingsPath { get; }

        public string ReceiptPath { get; }

        public string ProfilePath { get; }

        public SteamVrSettingsTrackerNeutralizationBackend CreateBackend() =>
            new(new FakeDriverLifecycle(SettingsPath), ReceiptPath);

        public ProductionInternalDriverSessionRuntime CreateRuntime() =>
            new(
                new InternalDriverSessionOptions(),
                new InternalDriverResolvedPaths(
                    Path.Combine(_root, "internal-driver.json"),
                    ProfilePath,
                    Path.Combine(_root, "driver_ltb"),
                    Path.Combine(_root, "session.jsonl"),
                    Path.Combine(_root, "driver-receipts.json")),
                new FakeDriverLifecycle(SettingsPath));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeDriverLifecycle : ISteamVrDriverLifecycle
    {
        private readonly SteamVrPaths _paths;

        public FakeDriverLifecycle(string settingsPath)
        {
            var root = Path.GetDirectoryName(settingsPath)!;
            _paths = new SteamVrPaths(
                Path.Combine(root, "openvrpaths.vrpath"),
                Path.Combine(root, "runtime"),
                root,
                Path.Combine(root, "vrpathreg"),
                settingsPath);
        }

        public ValueTask<SteamVrPaths> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_paths);

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
