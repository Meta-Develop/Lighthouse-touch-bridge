using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class PhysicalTrackerRoleSettingsTests
{
    private const string LeftTrackerPath =
        "/devices/lighthouse/LHR-LEFT-EXACT";
    private const string RightTrackerPath =
        "/devices/nonstandard_driver/non-obvious_serial.07";
    private const string UnrelatedTrackerPath =
        "/devices/lighthouse/LHR-UNRELATED";

    [Fact]
    public void NeutralizeCreatesOnlyExactTargetsAndRestoreRemovesIntroducedSection()
    {
        using var sandbox = SettingsSandbox.FromText(
            """
            {
              "steamvr": {
                "activateMultipleDrivers": true,
                "custom": 17
              },
              "TrackingOverrides": {
                "/devices/vmt/VMT_1": "/user/hand/left"
              },
              "dashboard": {
                "recentApps": ["keep", null, 3, false]
              }
            }
            """);
        var originalRoot = ReadRoot(sandbox.SettingsPath);
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());

        Assert.True(neutralization.SettingsChanged);
        Assert.Equal(
            SteamVrSettingsOperation.NeutralizePhysicalTrackerRoles,
            neutralization.Operation);
        Assert.NotNull(neutralization.PhysicalTrackerRoleState);
        Assert.NotNull(neutralization.BackupFilePath);
        Assert.Equal(
            originalBytes,
            File.ReadAllBytes(neutralization.BackupFilePath!));

        var neutralizedRoot = ReadRoot(sandbox.SettingsPath);
        var trackers = Assert.IsType<JsonObject>(neutralizedRoot["trackers"]);
        Assert.Equal(2, trackers.Count);
        Assert.Equal("TrackerRole_None", trackers[LeftTrackerPath]!.GetValue<string>());
        Assert.Equal("TrackerRole_None", trackers[RightTrackerPath]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            originalRoot["steamvr"],
            neutralizedRoot["steamvr"]));
        Assert.True(JsonNode.DeepEquals(
            originalRoot["TrackingOverrides"],
            neutralizedRoot["TrackingOverrides"]));
        Assert.True(JsonNode.DeepEquals(
            originalRoot["dashboard"],
            neutralizedRoot["dashboard"]));

        var restoration = manager.RestorePhysicalTrackerRoles(neutralization);

        Assert.True(restoration.SettingsChanged);
        Assert.Equal(
            SteamVrSettingsOperation.RestorePhysicalTrackerRoles,
            restoration.Operation);
        var restoredRoot = ReadRoot(sandbox.SettingsPath);
        Assert.False(restoredRoot.ContainsKey("trackers"));
        Assert.True(JsonNode.DeepEquals(originalRoot, restoredRoot));
        AssertNoTransientResidue(sandbox);
    }

    [Theory]
    [InlineData("\"TrackerRole_LeftFoot\"", "{\"nested\":[1,null,\"x\"]}")]
    [InlineData("17", "null")]
    [InlineData("[true,{\"x\":\"y\"}]", "false")]
    public void RestorePreservesArbitraryPriorValuesAndUnrelatedLaterChanges(
        string leftPriorJson,
        string rightPriorJson)
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": {{leftPriorJson}},
                "{{RightTrackerPath}}": {{rightPriorJson}},
                "{{UnrelatedTrackerPath}}": {
                  "role": "preserve",
                  "values": [1, null, false]
                }
              },
              "TrackingOverrides": {
                "/devices/vmt/VMT_1": "/user/hand/left"
              },
              "steamvr": {
                "activateMultipleDrivers": true,
                "custom": "preserve"
              },
              "other": {
                "nested": 23
              }
            }
            """);
        var expectedLeft = JsonNode.Parse(leftPriorJson);
        var expectedRight = JsonNode.Parse(rightPriorJson);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());

        var afterApply = ReadRoot(sandbox.SettingsPath);
        afterApply["laterExternalData"] = new JsonObject
        {
            ["winner"] = true,
            ["value"] = 42,
        };
        WriteRoot(sandbox.SettingsPath, afterApply);

        var restoration = manager.RestorePhysicalTrackerRoles(neutralization);

        Assert.True(restoration.SettingsChanged);
        var restoredRoot = ReadRoot(sandbox.SettingsPath);
        var trackers = Assert.IsType<JsonObject>(restoredRoot["trackers"]);
        Assert.True(trackers.ContainsKey(LeftTrackerPath));
        Assert.True(trackers.ContainsKey(RightTrackerPath));
        Assert.True(JsonNode.DeepEquals(expectedLeft, trackers[LeftTrackerPath]));
        Assert.True(JsonNode.DeepEquals(expectedRight, trackers[RightTrackerPath]));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(
                """
                {
                  "role": "preserve",
                  "values": [1, null, false]
                }
                """),
            trackers[UnrelatedTrackerPath]));
        Assert.True(restoredRoot["laterExternalData"]!["winner"]!.GetValue<bool>());
        Assert.Equal(42, restoredRoot["laterExternalData"]!["value"]!.GetValue<int>());
        Assert.Equal(
            "/user/hand/left",
            restoredRoot["TrackingOverrides"]!["/devices/vmt/VMT_1"]!.GetValue<string>());
        Assert.Equal(
            "preserve",
            restoredRoot["steamvr"]!["custom"]!.GetValue<string>());
        Assert.Equal(23, restoredRoot["other"]!["nested"]!.GetValue<int>());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void NeutralizePreservesUnrelatedTrackerRolesAndSettings()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{UnrelatedTrackerPath}}": "TrackerRole_Waist",
                "/devices/other_driver/keep-me": null
              },
              "TrackingOverrides": {
                "/devices/vmt/VMT_1": "/user/hand/left",
                "/devices/vmt/VMT_2": "/user/hand/right"
              },
              "steamvr": {
                "allowAsyncReprojection": false,
                "supersampleScale": 1.25
              },
              "dashboard": {
                "enableDashboard": true
              },
              "customTopLevel": [1, "two", null, false]
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        _ = manager.NeutralizePhysicalTrackerRoles(Targets());

        var root = ReadRoot(sandbox.SettingsPath);
        var trackers = Assert.IsType<JsonObject>(root["trackers"]);
        Assert.Equal("TrackerRole_None", trackers[LeftTrackerPath]!.GetValue<string>());
        Assert.Equal("TrackerRole_None", trackers[RightTrackerPath]!.GetValue<string>());
        Assert.Equal(
            "TrackerRole_Waist",
            trackers[UnrelatedTrackerPath]!.GetValue<string>());
        Assert.True(trackers.ContainsKey("/devices/other_driver/keep-me"));
        Assert.Null(trackers["/devices/other_driver/keep-me"]);
        Assert.Equal(
            "/user/hand/left",
            root["TrackingOverrides"]!["/devices/vmt/VMT_1"]!.GetValue<string>());
        Assert.Equal(
            "/user/hand/right",
            root["TrackingOverrides"]!["/devices/vmt/VMT_2"]!.GetValue<string>());
        Assert.False(root["steamvr"]!["allowAsyncReprojection"]!.GetValue<bool>());
        Assert.Equal(1.25, root["steamvr"]!["supersampleScale"]!.GetValue<double>());
        Assert.True(root["dashboard"]!["enableDashboard"]!.GetValue<bool>());
        Assert.Equal("two", root["customTopLevel"]![1]!.GetValue<string>());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RecoveryStateDistinguishesAbsentEntryFromExplicitNull()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{RightTrackerPath}}": null
              }
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());

        var state = Assert.IsType<PhysicalTrackerRoleState>(
            neutralization.PhysicalTrackerRoleState);
        Assert.True(state.TrackersSectionWasPresent);
        Assert.Equal(LeftTrackerPath, state.LeftTracker.RegisteredDevicePath);
        Assert.False(state.LeftTracker.WasPresent);
        Assert.Null(state.LeftTracker.PreviousValue);
        Assert.Equal(RightTrackerPath, state.RightTracker.RegisteredDevicePath);
        Assert.True(state.RightTracker.WasPresent);
        Assert.True(state.RightTracker.PreviousValue.HasValue);
        Assert.Equal(
            JsonValueKind.Null,
            state.RightTracker.PreviousValue.Value.ValueKind);
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void DriftInspectionReportsUnchangedNeutralTargetsWithoutWriting()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{RightTrackerPath}}": "TrackerRole_RightFoot",
                "{{UnrelatedTrackerPath}}": "TrackerRole_Waist"
              },
              "keep": true
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var filesBeforeInspection = ReadAllFiles(sandbox.DirectoryPath);
        var backupsBeforeInspection = manager.FindRecoveryBackups().ToArray();

        var drift = manager.InspectPhysicalTrackerRoleDrift(neutralization);

        Assert.False(drift.HasDrift);
        Assert.Same(
            neutralization.PhysicalTrackerRoleState!.Targets,
            drift.Targets);
        Assert.Equal(LeftTrackerPath, drift.LeftTracker.RegisteredDevicePath);
        Assert.Equal(
            TrackerRoleDriftStatus.UnchangedNeutral,
            drift.LeftTracker.Status);
        Assert.Equal("TrackerRole_None", drift.LeftTracker.ObservedRole);
        Assert.False(drift.LeftTracker.HasDrift);
        Assert.Equal(RightTrackerPath, drift.RightTracker.RegisteredDevicePath);
        Assert.Equal(
            TrackerRoleDriftStatus.UnchangedNeutral,
            drift.RightTracker.Status);
        Assert.Equal("TrackerRole_None", drift.RightTracker.ObservedRole);
        Assert.False(drift.RightTracker.HasDrift);
        Assert.Equal(
            backupsBeforeInspection,
            manager.FindRecoveryBackups());
        AssertFileSetEqual(
            filesBeforeInspection,
            ReadAllFiles(sandbox.DirectoryPath));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void DriftInspectionReportsPerHandChangedAndMissingWithoutRewriting()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{RightTrackerPath}}": "TrackerRole_RightFoot",
                "{{UnrelatedTrackerPath}}": "TrackerRole_Waist"
              },
              "keep": true
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var current = ReadRoot(sandbox.SettingsPath);
        current["trackers"]![LeftTrackerPath] = "TrackerRole_LeftShoulder";
        _ = current["trackers"]!.AsObject().Remove(RightTrackerPath);
        current["trackers"]![UnrelatedTrackerPath] = "TrackerRole_Chest";
        current["externalWriter"] = "preserve";
        WriteRoot(sandbox.SettingsPath, current);
        var filesBeforeInspection = ReadAllFiles(sandbox.DirectoryPath);

        var drift = manager.InspectPhysicalTrackerRoleDrift(neutralization);

        Assert.True(drift.HasDrift);
        Assert.Equal(TrackerRoleDriftStatus.Changed, drift.LeftTracker.Status);
        Assert.Equal("TrackerRole_LeftShoulder", drift.LeftTracker.ObservedRole);
        Assert.True(drift.LeftTracker.HasDrift);
        Assert.Equal(TrackerRoleDriftStatus.Missing, drift.RightTracker.Status);
        Assert.Null(drift.RightTracker.ObservedRole);
        Assert.True(drift.RightTracker.HasDrift);
        AssertFileSetEqual(
            filesBeforeInspection,
            ReadAllFiles(sandbox.DirectoryPath));
        var unchanged = ReadRoot(sandbox.SettingsPath);
        Assert.Equal(
            "TrackerRole_Chest",
            unchanged["trackers"]![UnrelatedTrackerPath]!.GetValue<string>());
        Assert.Equal(
            "preserve",
            unchanged["externalWriter"]!.GetValue<string>());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void DriftInspectionDoesNotExposeNonStringSettingsContent()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var current = ReadRoot(sandbox.SettingsPath);
        current["trackers"]![LeftTrackerPath] = new JsonObject
        {
            ["private"] = new JsonArray("not", "presentation", "data"),
        };
        WriteRoot(sandbox.SettingsPath, current);
        var bytesBeforeInspection = File.ReadAllBytes(sandbox.SettingsPath);

        var drift = manager.InspectPhysicalTrackerRoleDrift(neutralization);

        Assert.Equal(TrackerRoleDriftStatus.Changed, drift.LeftTracker.Status);
        Assert.Null(drift.LeftTracker.ObservedRole);
        Assert.Equal(
            TrackerRoleDriftStatus.UnchangedNeutral,
            drift.RightTracker.Status);
        Assert.Equal(bytesBeforeInspection, File.ReadAllBytes(sandbox.SettingsPath));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void DriftInspectionRejectsRecoveryPointFromAnotherSettingsPath()
    {
        using var first = SettingsSandbox.FromText("{\n  \"first\": true\n}\n");
        using var second = SettingsSandbox.FromText("{\n  \"second\": true\n}\n");
        var firstManager = new SteamVrSettingsManager(first.SettingsPath);
        var secondManager = new SteamVrSettingsManager(second.SettingsPath);
        var neutralization = firstManager.NeutralizePhysicalTrackerRoles(Targets());
        var secondBytes = File.ReadAllBytes(second.SettingsPath);

        Assert.Throws<ArgumentException>(() =>
            secondManager.InspectPhysicalTrackerRoleDrift(neutralization));

        Assert.Equal(secondBytes, File.ReadAllBytes(second.SettingsPath));
        Assert.Empty(secondManager.FindRecoveryBackups());
        AssertNoTransientResidue(first);
        AssertNoTransientResidue(second);
    }

    [Fact]
    public void ApplyAndRestoreAreIdempotentWithoutExtraBackups()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"other\": true\n}\n");
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var firstApply = manager.NeutralizePhysicalTrackerRoles(Targets());
        var secondApply = manager.NeutralizePhysicalTrackerRoles(Targets());

        Assert.True(firstApply.SettingsChanged);
        Assert.False(secondApply.SettingsChanged);
        Assert.Null(secondApply.BackupFilePath);
        Assert.NotNull(secondApply.PhysicalTrackerRoleState);
        Assert.Single(manager.FindRecoveryBackups());

        var noOpRestore = manager.RestorePhysicalTrackerRoles(secondApply);

        Assert.False(noOpRestore.SettingsChanged);
        Assert.Null(noOpRestore.BackupFilePath);
        Assert.Single(manager.FindRecoveryBackups());

        var firstRestore = manager.RestorePhysicalTrackerRoles(firstApply);
        var secondRestore = manager.RestorePhysicalTrackerRoles(firstApply);

        Assert.True(firstRestore.SettingsChanged);
        Assert.False(secondRestore.SettingsChanged);
        Assert.Null(secondRestore.BackupFilePath);
        Assert.False(ReadRoot(sandbox.SettingsPath).ContainsKey("trackers"));
        Assert.Equal(2, manager.FindRecoveryBackups().Count);
        AssertNoTransientResidue(sandbox);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("driver/serial")]
    [InlineData("/user/hand/left")]
    [InlineData("/devices/lighthouse")]
    [InlineData("/devices//LHR-INVALID")]
    [InlineData("/devices/lighthouse/../LHR-INVALID")]
    [InlineData("/devices/lighthouse/LHR-INVALID/")]
    [InlineData("/devices/lighthouse/LHR INVALID")]
    [InlineData("/devices/lighthouse/LHR-INVALID\n")]
    public void TargetsRejectInvalidCanonicalDevicePathsBeforeMutation(string? invalidPath)
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        Assert.ThrowsAny<ArgumentException>(() =>
        {
            var targets = new PhysicalTrackerRoleTargets(
                invalidPath!,
                RightTrackerPath);
            _ = manager.NeutralizePhysicalTrackerRoles(targets);
        });

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void TargetsRejectDuplicatePathsBeforeMutation()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        Assert.Throws<ArgumentException>(() =>
        {
            var targets = new PhysicalTrackerRoleTargets(
                LeftTrackerPath,
                LeftTrackerPath);
            _ = manager.NeutralizePhysicalTrackerRoles(targets);
        });

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void ConcurrentManagersAreSerializedAndContenderFailsBounded()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        SteamVrSettingsLockException? contention = null;
        var contender = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            afterAtomicWrite: null,
            lockTimeout: TimeSpan.FromMilliseconds(40));
        var winner = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            afterAtomicWrite: _ =>
            {
                contention = Assert.Throws<SteamVrSettingsLockException>(() =>
                    contender.NeutralizePhysicalTrackerRoles(Targets()));
            });

        var neutralization = winner.NeutralizePhysicalTrackerRoles(Targets());

        Assert.True(neutralization.SettingsChanged);
        Assert.NotNull(contention);
        Assert.Equal(TimeSpan.FromMilliseconds(40), contention.Timeout);
        var trackers = Assert.IsType<JsonObject>(
            ReadRoot(sandbox.SettingsPath)["trackers"]);
        Assert.Equal("TrackerRole_None", trackers[LeftTrackerPath]!.GetValue<string>());
        Assert.Equal("TrackerRole_None", trackers[RightTrackerPath]!.GetValue<string>());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void PreCommitExternalWriterWinsAndNoLtbWriteOccurs()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var externalWinner = Encoding.UTF8.GetBytes(
            "{\n  \"externalWinner\": \"before-commit\"\n}\n");
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            afterAtomicWrite: null,
            beforeFinalChangeCheck: path => File.WriteAllBytes(path, externalWinner));

        var failure = Assert.Throws<IOException>(() =>
            manager.NeutralizePhysicalTrackerRoles(Targets()));

        Assert.IsNotType<SteamVrSettingsUpdateException>(failure);
        Assert.Contains("changed during the update", failure.Message, StringComparison.Ordinal);
        Assert.Equal(externalWinner, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Single(manager.FindRecoveryBackups());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void PostWriteFailureAutomaticallyRestoresOriginalBytes()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            _ => throw new IOException("Simulated post-write failure."));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(() =>
            manager.NeutralizePhysicalTrackerRoles(Targets()));

        Assert.True(failure.OriginalRestored);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(failure.BackupFilePath));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void PostWriteExternalWinnerIsNeverOverwrittenByAutomaticRestore()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": 1\n}\n");
        var externalWinner = Encoding.UTF8.GetBytes(
            "{\n  \"externalWinner\": \"after-apply\"\n}\n");
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            path => File.WriteAllBytes(path, externalWinner));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(() =>
            manager.NeutralizePhysicalTrackerRoles(Targets()));

        Assert.False(failure.OriginalRestored);
        Assert.Contains("later writer", failure.Message, StringComparison.Ordinal);
        Assert.Equal(externalWinner, File.ReadAllBytes(sandbox.SettingsPath));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RestoreRefusesUnexpectedTargetChangeBeforeBackupOrMutation()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{RightTrackerPath}}": null
              },
              "keep": 1
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var externallyChanged = ReadRoot(sandbox.SettingsPath);
        externallyChanged["trackers"]![LeftTrackerPath] = "TrackerRole_Waist";
        externallyChanged["externalWinner"] = true;
        WriteRoot(sandbox.SettingsPath, externallyChanged);
        var backupsBeforeRestore = manager.FindRecoveryBackups().ToArray();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            manager.RestorePhysicalTrackerRoles(neutralization));

        Assert.Contains("changed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            backupsBeforeRestore,
            manager.FindRecoveryBackups());
        var current = ReadRoot(sandbox.SettingsPath);
        Assert.Equal(
            "TrackerRole_Waist",
            current["trackers"]![LeftTrackerPath]!.GetValue<string>());
        Assert.Equal(
            "TrackerRole_None",
            current["trackers"]![RightTrackerPath]!.GetValue<string>());
        Assert.True(current["externalWinner"]!.GetValue<bool>());
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RestorePostWriteFailureAutomaticallyRestoresNeutralizedState()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{RightTrackerPath}}": null
              },
              "keep": 1
            }
            """);
        var applyManager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = applyManager.NeutralizePhysicalTrackerRoles(Targets());
        var neutralizedBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var failingRestoreManager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            _ => throw new IOException("Simulated restore validation failure."));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(() =>
            failingRestoreManager.RestorePhysicalTrackerRoles(neutralization));

        Assert.True(failure.OriginalRestored);
        Assert.Equal(neutralizedBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(neutralizedBytes, File.ReadAllBytes(failure.BackupFilePath));
        AssertNoTransientResidue(sandbox);

        var successfulRestore = applyManager.RestorePhysicalTrackerRoles(neutralization);
        Assert.True(successfulRestore.SettingsChanged);
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RestorePostWriteExternalWinnerIsNeverOverwritten()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot",
                "{{RightTrackerPath}}": null
              },
              "keep": 1
            }
            """);
        var applyManager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = applyManager.NeutralizePhysicalTrackerRoles(Targets());
        var externalWinner = Encoding.UTF8.GetBytes(
            "{\n  \"externalWinner\": \"after-restore\"\n}\n");
        var restoreManager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            path => File.WriteAllBytes(path, externalWinner));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(() =>
            restoreManager.RestorePhysicalTrackerRoles(neutralization));

        Assert.False(failure.OriginalRestored);
        Assert.Contains("later writer", failure.Message, StringComparison.Ordinal);
        Assert.Equal(externalWinner, File.ReadAllBytes(sandbox.SettingsPath));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RollbackRestoresExactPreNeutralizationBytes()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": {
                  "custom": [1, null, false]
                },
                "{{RightTrackerPath}}": 17
              },
              "keep": "byte-for-byte"
            }
            """);
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var neutralizedBytes = File.ReadAllBytes(sandbox.SettingsPath);

        var rollback = manager.Rollback(neutralization);

        Assert.True(rollback.SettingsChanged);
        Assert.Equal(
            SteamVrSettingsOperation.RestoreBackup,
            rollback.Operation);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(neutralizedBytes, File.ReadAllBytes(rollback.BackupFilePath!));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RecoveryDiscoveryAndRecoveryRestoreInterruptedTransactionBackup()
    {
        using var sandbox = SettingsSandbox.FromText(
            $$"""
            {
              "trackers": {
                "{{LeftTrackerPath}}": "TrackerRole_LeftFoot"
              },
              "keep": {
                "nested": true
              }
            }
            """);
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var neutralization = manager.NeutralizePhysicalTrackerRoles(Targets());
        var neutralizedBytes = File.ReadAllBytes(sandbox.SettingsPath);

        Assert.Contains(
            neutralization.BackupFilePath!,
            manager.FindRecoveryBackups());

        File.WriteAllText(
            sandbox.SettingsPath,
            "{\"interrupted\":",
            new UTF8Encoding(false));

        var recovery = manager.RecoverFromBackup(neutralization.BackupFilePath!);

        Assert.True(recovery.SettingsChanged);
        Assert.Equal(
            SteamVrSettingsOperation.RestoreBackup,
            recovery.Operation);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.NotNull(recovery.BackupFilePath);
        Assert.Contains(
            neutralization.BackupFilePath!,
            manager.FindRecoveryBackups());
        Assert.Contains(
            recovery.BackupFilePath!,
            manager.FindRecoveryBackups());
        Assert.NotEqual(neutralizedBytes, File.ReadAllBytes(recovery.BackupFilePath!));
        AssertNoTransientResidue(sandbox);
    }

    [Fact]
    public void RecoveryDiscoveryReturnsOrderedMetadataWithoutReadingContents()
    {
        using var sandbox = SettingsSandbox.FromText(
            "{\n  \"current\": \"must-not-change\"\n}\n");
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var settingsBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var prefix = sandbox.SettingsPath + ".ltb-backup";
        var backupBytes = new Dictionary<string, byte[]>
        {
            [prefix] = Array.Empty<byte>(),
            [prefix + ".1"] = Encoding.UTF8.GetBytes("{\"malformed\":"),
            [prefix + ".10"] = Encoding.UTF8.GetBytes("not json"),
            [prefix + ".2"] = Encoding.UTF8.GetBytes(
                "{\n  \"candidate\": \"never-auto-restored\"\n}\n"),
        };
        foreach (var pair in backupBytes)
        {
            File.WriteAllBytes(pair.Key, pair.Value);
        }

        var expectedWriteTime = new DateTime(
            2026,
            7,
            28,
            3,
            4,
            5,
            DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(prefix + ".1", expectedWriteTime);

        SteamVrSettingsRecoveryDiscovery discovery;
        using (var contentLock = new FileStream(
                   prefix + ".1",
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            discovery = manager.DiscoverRecoveryBackups();
        }

        Assert.Equal(sandbox.SettingsPath, discovery.SettingsFilePath);
        Assert.Equal(
            [
                "steamvr.vrsettings.ltb-backup",
                "steamvr.vrsettings.ltb-backup.1",
                "steamvr.vrsettings.ltb-backup.10",
                "steamvr.vrsettings.ltb-backup.2",
            ],
            discovery.Candidates.Select(candidate => candidate.FileName));
        Assert.Equal(
            [0, 1, 10, 2],
            discovery.Candidates.Select(candidate => candidate.SequenceNumber));
        foreach (var candidate in discovery.Candidates)
        {
            Assert.Equal(
                backupBytes[candidate.BackupFilePath].LongLength,
                candidate.LengthBytes);
        }

        Assert.Equal(
            new DateTimeOffset(expectedWriteTime),
            discovery.Candidates.Single(candidate =>
                candidate.SequenceNumber == 1).LastWriteTimeUtc);
        Assert.Equal(settingsBytes, File.ReadAllBytes(sandbox.SettingsPath));
        foreach (var pair in backupBytes)
        {
            Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }

        Assert.Equal(
            discovery.Candidates.Select(candidate => candidate.BackupFilePath),
            manager.FindRecoveryBackups());
    }

    [Fact]
    public void RecoveryDiscoveryExcludesInvalidNamesStagingAndNonFiles()
    {
        using var sandbox = SettingsSandbox.FromText("{\n  \"keep\": true\n}\n");
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var prefix = sandbox.SettingsPath + ".ltb-backup";
        File.WriteAllText(prefix, "recognized", Encoding.UTF8);
        foreach (var invalidPath in new[]
                 {
                     sandbox.SettingsPath + ".ltb-backup-write",
                     sandbox.SettingsPath + ".ltb-backup-write.1",
                     sandbox.SettingsPath + ".ltb-write",
                     prefix + ".0",
                     prefix + ".-1",
                     prefix + ".+1",
                     prefix + ".01",
                     prefix + ".1.tmp",
                     prefix + "x",
                 })
        {
            File.WriteAllText(invalidPath, "not a candidate", Encoding.UTF8);
        }

        Directory.CreateDirectory(prefix + ".2");
        var symlinkPath = prefix + ".3";
        var symlinkCreated = TryCreateFileSymbolicLink(symlinkPath, prefix);

        var discovery = manager.DiscoverRecoveryBackups();

        var candidate = Assert.Single(discovery.Candidates);
        Assert.Equal(prefix, candidate.BackupFilePath);
        Assert.Equal(0, candidate.SequenceNumber);
        Assert.Equal(
            [prefix],
            manager.FindRecoveryBackups());
        if (symlinkCreated)
        {
            Assert.Throws<ArgumentException>(() =>
                manager.RecoverFromBackup(symlinkPath));
        }

        Assert.Equal(
            "{\n  \"keep\": true\n}\n",
            File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8));
    }

    private static PhysicalTrackerRoleTargets Targets() =>
        new(LeftTrackerPath, RightTrackerPath);

    private static JsonObject ReadRoot(string path) =>
        JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();

    private static void WriteRoot(string path, JsonObject root) =>
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

    private static IReadOnlyDictionary<string, byte[]> ReadAllFiles(
        string directoryPath) =>
        Directory
            .EnumerateFiles(directoryPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => path,
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void AssertFileSetEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var pair in expected)
        {
            Assert.Equal(pair.Value, actual[pair.Key]);
        }
    }

    private static bool TryCreateFileSymbolicLink(
        string symbolicLinkPath,
        string targetPath)
    {
        try
        {
            _ = File.CreateSymbolicLink(symbolicLinkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is PlatformNotSupportedException or
                UnauthorizedAccessException or
                IOException)
        {
            return false;
        }
    }

    private static void AssertNoTransientResidue(SettingsSandbox sandbox)
    {
        Assert.Empty(Directory.EnumerateFiles(
            sandbox.DirectoryPath,
            "steamvr.vrsettings.ltb-write*"));
        Assert.Empty(Directory.EnumerateFiles(
            sandbox.DirectoryPath,
            "steamvr.vrsettings.ltb-backup-write*"));
        var lockPath = sandbox.SettingsPath + ".ltb-lock";
        using var releasedLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    private sealed class SettingsSandbox : IDisposable
    {
        private SettingsSandbox(string text)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ltb-physical-tracker-role-settings-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            SettingsPath = Path.Combine(DirectoryPath, "steamvr.vrsettings");
            File.WriteAllText(
                SettingsPath,
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string DirectoryPath { get; }

        public string SettingsPath { get; }

        public static SettingsSandbox FromText(string text) => new(text);

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
