using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class TrackingOverrideSettingsTests
{
    private const string LeftVmtPath = "/devices/vmt/VMT_1";
    private const string RightVmtPath = "/devices/vmt/VMT_2";

    [Fact]
    public void EnableMergesFixturePreservesTypesAndRollsBackByteForByte()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var activation = manager.EnableOverride(LeftBinding());

        Assert.True(activation.SettingsChanged);
        Assert.Equal(SteamVrSettingsOperation.EnableTrackingOverride, activation.Operation);
        Assert.NotNull(activation.BackupFilePath);
        Assert.Equal(originalBytes, File.ReadAllBytes(activation.BackupFilePath!));

        using (var written = JsonDocument.Parse(File.ReadAllBytes(sandbox.SettingsPath)))
        {
            var root = written.RootElement;
            Assert.True(root.GetProperty("steamvr")
                .GetProperty("activateMultipleDrivers")
                .GetBoolean());
            Assert.False(root.GetProperty("steamvr")
                .GetProperty("allowAsyncReprojection")
                .GetBoolean());
            Assert.Equal(1.25, root.GetProperty("steamvr")
                .GetProperty("supersampleScale")
                .GetDouble());
            Assert.Equal("preserve-me", root.GetProperty("steamvr")
                .GetProperty("customString")
                .GetString());

            var overrides = root.GetProperty("TrackingOverrides");
            Assert.Equal(
                TrackingOverrideBinding.LeftHandPath,
                overrides.GetProperty(LeftVmtPath).GetString());
            Assert.Equal(
                TrackingOverrideBinding.RightHandPath,
                overrides.GetProperty(RightVmtPath).GetString());

            var recentApps = root.GetProperty("dashboard").GetProperty("recentApps");
            Assert.Equal(JsonValueKind.String, recentApps[0].ValueKind);
            Assert.Equal(JsonValueKind.Null, recentApps[1].ValueKind);
            Assert.Equal(JsonValueKind.Number, recentApps[2].ValueKind);
            Assert.Equal(JsonValueKind.False, recentApps[3].ValueKind);
            Assert.Equal(
                "LHR-TEST0001",
                root.GetProperty("driver_lighthouse")
                    .GetProperty("fakeSerialForFixtureOnly")
                    .GetString());
        }

        var undoRollback = manager.Rollback(activation);

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.True(undoRollback.SettingsChanged);
        Assert.NotEqual(activation.BackupFilePath, undoRollback.BackupFilePath);
        Assert.Equal(2, manager.FindRecoveryBackups().Count);
    }

    [Fact]
    public void ReleaseRemovesOnlyExactMappingAndIsThenIdempotent()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "two-active-overrides.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var release = manager.ReleaseOverride(LeftBinding());

        Assert.True(release.SettingsChanged);
        Assert.Equal(originalBytes, File.ReadAllBytes(release.BackupFilePath!));
        using (var written = JsonDocument.Parse(File.ReadAllBytes(sandbox.SettingsPath)))
        {
            var root = written.RootElement;
            Assert.True(root.GetProperty("steamvr")
                .GetProperty("activateMultipleDrivers")
                .GetBoolean());
            Assert.False(root.GetProperty("steamvr")
                .GetProperty("allowAsyncReprojection")
                .GetBoolean());
            Assert.True(root.GetProperty("dashboard")
                .GetProperty("enableDashboard")
                .GetBoolean());

            var overrides = root.GetProperty("TrackingOverrides");
            Assert.False(overrides.TryGetProperty(LeftVmtPath, out _));
            Assert.Equal(
                TrackingOverrideBinding.RightHandPath,
                overrides.GetProperty(RightVmtPath).GetString());
        }

        var secondRelease = manager.ReleaseOverride(LeftBinding());

        Assert.False(secondRelease.SettingsChanged);
        Assert.Null(secondRelease.BackupFilePath);
        Assert.Single(manager.FindRecoveryBackups());
    }

    [Fact]
    public void ExactExistingActivationIsIdempotentWithoutCreatingBackup()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "two-active-overrides.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var result = manager.EnableOverride(LeftBinding());

        Assert.False(result.SettingsChanged);
        Assert.Null(result.BackupFilePath);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
    }

    [Theory]
    [InlineData(RightVmtPath, TrackingOverrideBinding.LeftHandPath)]
    [InlineData("/devices/vmt/VMT_3", TrackingOverrideBinding.RightHandPath)]
    public void EnableRejectsSourceOrHandOwnershipConflicts(
        string poseSourcePath,
        string semanticHandPath)
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        Assert.Throws<InvalidOperationException>(() => manager.EnableOverride(
            new TrackingOverrideBinding(poseSourcePath, semanticHandPath)));

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
    }

    [Fact]
    public void ExistingExactMappingStillRejectsSecondSourceForSameHand()
    {
        using var sandbox = SettingsSandbox.FromText(
            """
            {
              "steamvr": { "activateMultipleDrivers": true },
              "TrackingOverrides": {
                "/devices/vmt/VMT_1": "/user/hand/left",
                "/devices/vmt/VMT_3": "/user/hand/left"
              }
            }
            """);
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        Assert.Throws<InvalidOperationException>(
            () => manager.EnableOverride(LeftBinding()));

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
    }

    [Fact]
    public void FailedPostWriteValidationAutomaticallyRestoresOriginalBytes()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            _ => throw new IOException("Simulated post-write validation failure."));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(
            () => manager.EnableOverride(LeftBinding()));

        Assert.True(failure.OriginalRestored);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(failure.BackupFilePath));
        Assert.Empty(Directory.EnumerateFiles(
            sandbox.DirectoryPath,
            "steamvr.vrsettings.ltb-write*"));
    }

    [Fact]
    public void PostWriteExternalWinnerIsNeverOverwrittenByAutomaticRestore()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var externalWinner = Encoding.UTF8.GetBytes(
            "{\n  \"postWriteExternalWinner\": true\n}\n");
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            path => File.WriteAllBytes(path, externalWinner));

        var failure = Assert.Throws<SteamVrSettingsUpdateException>(
            () => manager.EnableOverride(LeftBinding()));

        Assert.False(failure.OriginalRestored);
        Assert.Contains("later writer", failure.Message, StringComparison.Ordinal);
        Assert.Equal(externalWinner, File.ReadAllBytes(sandbox.SettingsPath));
    }

    [Fact]
    public void RecoveryApiRestoresSelectedBackupAndBacksUpReplacedContent()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var activation = manager.EnableOverride(LeftBinding());
        var activeBytes = File.ReadAllBytes(sandbox.SettingsPath);

        var undoRecovery = manager.RecoverFromBackup(activation.BackupFilePath!);

        Assert.True(undoRecovery.SettingsChanged);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(activeBytes, File.ReadAllBytes(undoRecovery.BackupFilePath!));

        var redoRecovery = manager.Rollback(undoRecovery);

        Assert.True(redoRecovery.SettingsChanged);
        Assert.Equal(activeBytes, File.ReadAllBytes(sandbox.SettingsPath));
    }

    [Fact]
    public void RecoveryRejectsArbitraryNonSiblingOrWrongNameFiles()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var arbitrarySibling = Path.Combine(sandbox.DirectoryPath, "not-a-backup.json");
        File.WriteAllText(arbitrarySibling, "{}", Encoding.UTF8);
        var nestedDirectory = Path.Combine(sandbox.DirectoryPath, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var nestedFile = Path.Combine(
            nestedDirectory,
            "steamvr.vrsettings.ltb-backup");
        File.WriteAllText(nestedFile, "{}", Encoding.UTF8);

        Assert.Throws<ArgumentException>(() =>
            manager.RecoverFromBackup(arbitrarySibling));
        Assert.Throws<ArgumentException>(() =>
            manager.RecoverFromBackup(nestedFile));
    }

    [Fact]
    public void InvalidSettingsSectionFailsBeforeBackupOrMutation()
    {
        using var sandbox = SettingsSandbox.FromText(
            "{\n  \"steamvr\": [],\n  \"other\": true\n}\n");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var failure = Assert.Throws<InvalidDataException>(
            () => manager.EnableOverride(LeftBinding()));

        Assert.Contains("steamvr", failure.Message, StringComparison.Ordinal);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
    }

    [Fact]
    public void ReleaseRefusesToRemoveSourceMappedToAnotherHand()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        Assert.Throws<InvalidOperationException>(() => manager.ReleaseOverride(
            new TrackingOverrideBinding(
                RightVmtPath,
                TrackingOverrideBinding.LeftHandPath)));

        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Empty(manager.FindRecoveryBackups());
    }

    [Fact]
    public void SafeDisableReleasePreservesMalformedUnrelatedOverrideValues()
    {
        using var sandbox = SettingsSandbox.FromText(
            """
            {
              "steamvr": { "activateMultipleDrivers": true },
              "TrackingOverrides": {
                "/devices/vmt/VMT_1": "/user/hand/left",
                "/devices/legacy/broken-object": { "unexpected": true },
                "/devices/legacy/broken-null": null
              },
              "unrelated": 17
            }
            """);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var release = manager.ReleaseOverride(LeftBinding());

        Assert.True(release.SettingsChanged);
        using var written = JsonDocument.Parse(File.ReadAllBytes(sandbox.SettingsPath));
        var root = written.RootElement;
        var overrides = root.GetProperty("TrackingOverrides");
        Assert.False(overrides.TryGetProperty(LeftVmtPath, out _));
        Assert.True(overrides.GetProperty("/devices/legacy/broken-object")
            .GetProperty("unexpected")
            .GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            overrides.GetProperty("/devices/legacy/broken-null").ValueKind);
        Assert.Equal(17, root.GetProperty("unrelated").GetInt32());
    }

    [Fact]
    public void RecoveryRestoresValidBackupOverTruncatedCurrentAndPreservesRawUndo()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var originalBytes = File.ReadAllBytes(sandbox.SettingsPath);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);
        var activation = manager.EnableOverride(LeftBinding());
        var truncatedBytes = Encoding.UTF8.GetBytes("{\"truncated\":");
        File.WriteAllBytes(sandbox.SettingsPath, truncatedBytes);

        var undoRecovery = manager.RecoverFromBackup(activation.BackupFilePath!);

        Assert.True(undoRecovery.SettingsChanged);
        Assert.Equal(originalBytes, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Equal(truncatedBytes, File.ReadAllBytes(undoRecovery.BackupFilePath!));

        var redoRecovery = manager.Rollback(undoRecovery);

        Assert.True(redoRecovery.SettingsChanged);
        Assert.Equal(truncatedBytes, File.ReadAllBytes(sandbox.SettingsPath));
    }

    [Fact]
    public void PreWriteChangeDetectionNeverRestoresOverExternalWinner()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var externalWinner = Encoding.UTF8.GetBytes(
            "{\n  \"externalWinner\": true\n}\n");
        var manager = new SteamVrSettingsManager(
            sandbox.SettingsPath,
            afterAtomicWrite: null,
            beforeFinalChangeCheck: path => File.WriteAllBytes(path, externalWinner));

        var failure = Assert.Throws<IOException>(
            () => manager.EnableOverride(LeftBinding()));

        Assert.IsNotType<SteamVrSettingsUpdateException>(failure);
        Assert.Contains("changed during the update", failure.Message, StringComparison.Ordinal);
        Assert.Equal(externalWinner, File.ReadAllBytes(sandbox.SettingsPath));
        Assert.Single(manager.FindRecoveryBackups());
    }

    [Fact]
    public void ConcurrentManagersAreSerializedAndContenderFailsBounded()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
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
                    contender.ReleaseOverride(new TrackingOverrideBinding(
                        RightVmtPath,
                        TrackingOverrideBinding.RightHandPath)));
            });

        var activation = winner.EnableOverride(LeftBinding());

        Assert.True(activation.SettingsChanged);
        Assert.NotNull(contention);
        Assert.Equal(TimeSpan.FromMilliseconds(40), contention.Timeout);
        using var written = JsonDocument.Parse(File.ReadAllBytes(sandbox.SettingsPath));
        var overrides = written.RootElement.GetProperty("TrackingOverrides");
        Assert.Equal(
            TrackingOverrideBinding.LeftHandPath,
            overrides.GetProperty(LeftVmtPath).GetString());
        Assert.Equal(
            TrackingOverrideBinding.RightHandPath,
            overrides.GetProperty(RightVmtPath).GetString());
    }

    [Fact]
    public void ExistingBackupAndStagingNamesAdvanceWithoutOverwriting()
    {
        using var sandbox = SettingsSandbox.FromFixture(
            "unrelated-settings.vrsettings.json");
        var backupPrefix = sandbox.SettingsPath + ".ltb-backup";
        File.WriteAllText(backupPrefix, "existing-zero", Encoding.UTF8);
        File.WriteAllText(backupPrefix + ".1", "existing-one", Encoding.UTF8);
        File.WriteAllText(
            sandbox.SettingsPath + ".ltb-backup-write",
            "stale-backup-staging",
            Encoding.UTF8);
        File.WriteAllText(
            sandbox.SettingsPath + ".ltb-write",
            "stale-atomic-staging",
            Encoding.UTF8);
        var manager = new SteamVrSettingsManager(sandbox.SettingsPath);

        var activation = manager.EnableOverride(LeftBinding());

        Assert.Equal(backupPrefix + ".2", activation.BackupFilePath);
        Assert.Equal("existing-zero", File.ReadAllText(backupPrefix, Encoding.UTF8));
        Assert.Equal("existing-one", File.ReadAllText(backupPrefix + ".1", Encoding.UTF8));
        Assert.Equal(
            "stale-backup-staging",
            File.ReadAllText(
                sandbox.SettingsPath + ".ltb-backup-write",
                Encoding.UTF8));
        Assert.Equal(
            "stale-atomic-staging",
            File.ReadAllText(sandbox.SettingsPath + ".ltb-write", Encoding.UTF8));
        Assert.Equal(3, manager.FindRecoveryBackups().Count);
    }

    [Fact]
    public void BindingAcceptsDiscoveredPathButOnlyKnownSemanticHands()
    {
        var binding = new TrackingOverrideBinding(
            "/devices/vmt/runtime-discovered-device",
            TrackingOverrideBinding.LeftHandPath);

        Assert.Equal(
            "/devices/vmt/runtime-discovered-device",
            binding.PoseSourceDevicePath);
        Assert.Throws<ArgumentException>(() => new TrackingOverrideBinding(
            LeftVmtPath,
            "/user/head"));
    }

    [Theory]
    [InlineData("/user/hand/left")]
    [InlineData("vmt/VMT_1")]
    [InlineData("/devices/vmt")]
    [InlineData("/devices//VMT_1")]
    [InlineData("/devices/vmt/../VMT_1")]
    [InlineData("/devices/vmt/VMT_1/")]
    [InlineData("/devices/vmt/VMT_1\n")]
    [InlineData("/devices/vmt/VMT 1")]
    public void BindingRejectsNonDeviceAndUnsafePoseSourcePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new TrackingOverrideBinding(
            path,
            TrackingOverrideBinding.LeftHandPath));
    }

    private static TrackingOverrideBinding LeftBinding() =>
        new(LeftVmtPath, TrackingOverrideBinding.LeftHandPath);

    private sealed class SettingsSandbox : IDisposable
    {
        private SettingsSandbox(string text)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ltb-steamvr-settings-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            SettingsPath = Path.Combine(DirectoryPath, "steamvr.vrsettings");
            File.WriteAllText(SettingsPath, text, new UTF8Encoding(false));
        }

        public string DirectoryPath { get; }

        public string SettingsPath { get; }

        public static SettingsSandbox FromText(string text) => new(text);

        public static SettingsSandbox FromFixture(
            string fixtureName,
            [CallerFilePath] string sourceFilePath = "")
        {
            var fixturePath = Path.Combine(
                Path.GetDirectoryName(sourceFilePath)!,
                "Fixtures",
                "SteamVrSettings",
                fixtureName);
            return new SettingsSandbox(File.ReadAllText(fixturePath, Encoding.UTF8));
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
