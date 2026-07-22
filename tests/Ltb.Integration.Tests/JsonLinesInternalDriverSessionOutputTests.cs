using System.Text.Json;
using Ltb.App;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.Integration.Tests;

public sealed class JsonLinesInternalDriverSessionOutputTests
{
    [Fact]
    public void VolatilePollTelemetryIsSuppressedWhileSafetyTransitionsArePreserved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        var baseline = ActiveSnapshot();

        using (var output = new JsonLinesInternalDriverSessionOutput(path))
        {
            output.Write(baseline);
            output.Write(baseline with
            {
                Left = baseline.Left with { PoseAge = TimeSpan.FromMilliseconds(7) },
                Right = baseline.Right with { PoseAge = TimeSpan.FromMilliseconds(9) },
                Feed = baseline.Feed with
                {
                    LastSuccessfulSequence = 102,
                    LastSuccessfulSendAge = TimeSpan.FromMilliseconds(3),
                    LastSuccessfulHeartbeatAge = TimeSpan.FromMilliseconds(4),
                },
            });

            var stateTransition = baseline with { State = InternalDriverSessionState.Reconnecting };
            output.Write(stateTransition);
            var readinessTransition = stateTransition with
            {
                Readiness = stateTransition.Readiness with { FeedReady = false },
            };
            output.Write(readinessTransition);
            var neutralTransition = readinessTransition with
            {
                Left = readinessTransition.Left with
                {
                    IsPublishing = false,
                    NeutralReason = InternalDriverNeutralReason.TrackerDisconnected,
                    Diagnostic = "The left tracker disconnected.",
                },
            };
            output.Write(neutralTransition);
            var feedTransition = neutralTransition with
            {
                Feed = neutralTransition.Feed with
                {
                    Readiness = DriverFeedReadiness.Reconnecting,
                    SessionId = new ProtocolSessionId(30, 40),
                    ReconnectAttempts = 1,
                    LastError = "The previous feed session closed.",
                },
            };
            output.Write(feedTransition);
            output.Write(feedTransition with
            {
                Diagnostic = "A fresh feed session is being established.",
            });
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public void RotationKeepsTheActiveFileAndOnlyTheConfiguredNumberOfArchives()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        const long maxFileSizeBytes = 16 * 1024;
        const int retainedFileCount = 2;
        var payload = new string('x', 12_000);

        using (var output = new JsonLinesInternalDriverSessionOutput(
            path,
            maxFileSizeBytes,
            retainedFileCount))
        {
            for (var index = 0; index < 10; index++)
            {
                output.Write(ActiveSnapshot() with
                {
                    Diagnostic = $"transition-{index}-{payload}",
                });
            }
        }

        var retainedPaths = Directory.GetFiles(directory.Path, "session.jsonl*");
        Assert.Equal(retainedFileCount + 1, retainedPaths.Length);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists($"{path}.1"));
        Assert.True(File.Exists($"{path}.2"));
        Assert.False(File.Exists($"{path}.3"));
        Assert.All(retainedPaths, retainedPath =>
            Assert.InRange(new FileInfo(retainedPath).Length, 1, maxFileSizeBytes));
    }

    [Fact]
    public void StartupPreservesExistingActiveLogAndPrunesOnlyExpiredNumberedArchives()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        const string activeContents = "existing-active-record\n";
        File.WriteAllText(path, activeContents);
        File.WriteAllText($"{path}.1", "current-one");
        File.WriteAllText($"{path}.2", "current-two");
        File.WriteAllText($"{path}.3", "expired-three");
        File.WriteAllText($"{path}.backup", "unrelated-backup");
        File.WriteAllText($"{path}.03", "unrelated-leading-zero");
        File.WriteAllText($"{path}.4.tmp", "unrelated-suffix");

        using (new JsonLinesInternalDriverSessionOutput(
            path,
            maxFileSizeBytes: 1024,
            retainedFileCount: 2))
        {
        }

        Assert.Equal(activeContents, File.ReadAllText(path));
        Assert.True(File.Exists($"{path}.1"));
        Assert.True(File.Exists($"{path}.2"));
        Assert.False(File.Exists($"{path}.3"));
        Assert.True(File.Exists($"{path}.backup"));
        Assert.True(File.Exists($"{path}.03"));
        Assert.True(File.Exists($"{path}.4.tmp"));
    }

    [Fact]
    public void EveryEmittedRecordIsOneCompleteJsonLine()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        var baseline = ActiveSnapshot();

        using (var output = new JsonLinesInternalDriverSessionOutput(path))
        {
            output.Write(baseline);
            output.Write(baseline with
            {
                Diagnostic = "A diagnostic with a newline\nand a quote \" remains one record.",
            });
            output.Write(baseline with
            {
                Feed = baseline.Feed with { LastError = "feed error: 雪" },
            });
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("state", out _));
            Assert.True(root.TryGetProperty("readiness", out _));
            Assert.True(root.TryGetProperty("left", out _));
            Assert.True(root.TryGetProperty("right", out _));
            Assert.True(root.TryGetProperty("feed", out _));
            Assert.True(root.TryGetProperty("driver", out var driver));
            Assert.Equal("driver_ltb-test", driver.GetProperty("staged_build_identity").GetString());
            Assert.Equal(
                "LTB-TOUCH-LEFT",
                driver.GetProperty("left_controller").GetProperty("serial_number").GetString());
            Assert.True(root.TryGetProperty("lighthouse_hmd", out var hmd));
            Assert.Equal("HMD-LIGHTHOUSE", hmd.GetProperty("stable_device_id").GetString());
            Assert.Equal(
                "RotationOnly",
                root.GetProperty("left").GetProperty("calibration").GetProperty("selected_mode").GetString());
            Assert.Equal(
                24,
                root.GetProperty("left").GetProperty("capture").GetProperty("sample_count").GetInt32());
            Assert.True(root.TryGetProperty("diagnostic", out _));
            Assert.True(root.TryGetProperty("remediation", out _));
        }
    }

    [Fact]
    public void FinalClearedSnapshotSerializesNullRuntimeProfileAndCaptureEvidence()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        using (var output = new JsonLinesInternalDriverSessionOutput(path))
        {
            output.Write(ActiveSnapshot());
            output.Write(InternalDriverSessionSnapshot.Initial);
        }

        var finalLine = File.ReadAllLines(path)[^1];
        using var document = JsonDocument.Parse(finalLine);
        var root = document.RootElement;
        Assert.Equal("Stopped", root.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("driver").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lighthouse_hmd").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("left").GetProperty("tracker_serial").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("left").GetProperty("calibration").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("left").GetProperty("capture").ValueKind);
        Assert.False(root.GetProperty("readiness").GetProperty("can_publish").GetBoolean());
    }

    [Fact]
    public void DisposeFlushesTheLastRecordReleasesTheFileAndRejectsFurtherWrites()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        var snapshot = ActiveSnapshot();
        var output = new JsonLinesInternalDriverSessionOutput(path);

        output.Write(snapshot);
        output.Dispose();
        output.Dispose();

        var line = Assert.Single(File.ReadAllLines(path));
        using (JsonDocument.Parse(line))
        {
        }

        using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.Throws<ObjectDisposedException>(() => output.Write(snapshot));
    }

    private static InternalDriverSessionSnapshot ActiveSnapshot()
    {
        var sessionId = new ProtocolSessionId(10, 20);
        return new InternalDriverSessionSnapshot(
            InternalDriverSessionState.Active,
            new InternalDriverSessionReadiness(
                PlatformSupported: true,
                SteamVrRunning: true,
                MetaBothHandsReady: true,
                TwoDistinctTrackersReady: true,
                ProfilesReady: true,
                DriverRegistered: true,
                DriverLoaded: true,
                FeedReady: true),
            ActiveHand(ProtocolHand.Left, "TRACKER-LEFT"),
            ActiveHand(ProtocolHand.Right, "TRACKER-RIGHT"),
            new InternalDriverFeedSnapshot(
                DriverFeedReadiness.Ready,
                sessionId,
                LastSuccessfulSequence: 101,
                LastSuccessfulSendAge: TimeSpan.FromMilliseconds(2),
                LastSuccessfulHeartbeatAge: TimeSpan.FromMilliseconds(2),
                ReconnectAttempts: 0,
                LastError: null),
            RestartRequired: false,
            "Both hands are publishing.",
            "No remediation is required.")
        {
            Driver = new InternalDriverDriverEvidence(
                "driver_ltb-test",
                new InternalDriverLoadedControllerEvidence(
                    "LTB-TOUCH-LEFT",
                    "driver_ltb-test"),
                new InternalDriverLoadedControllerEvidence(
                    "LTB-TOUCH-RIGHT",
                    "driver_ltb-test")),
            LighthouseHmd = new InternalDriverLighthouseHmdEvidence(
                "HMD-LIGHTHOUSE",
                "/devices/HMD-LIGHTHOUSE",
                "lighthouse",
                "lighthouse",
                "Example",
                "Lighthouse HMD"),
        };
    }

    private static InternalDriverHandSnapshot ActiveHand(ProtocolHand hand, string trackerSerial) =>
        new InternalDriverHandSnapshot(
            hand,
            trackerSerial,
            TrackerConnected: true,
            TrackerTracked: true,
            MetaLinkReadiness.Ready,
            MetaInputsValid: true,
            InternalDriverProfileReadiness.Reused,
            PoseAge: TimeSpan.FromMilliseconds(2),
            IsPublishing: true,
            InternalDriverNeutralReason.None,
            "The hand is publishing.")
        {
            Calibration = new InternalDriverCalibrationEvidence(
                2,
                InternalDriverCalibrationMode.RotationOnly,
                "held-out rotation evidence accepted",
                8.5d,
                new InternalDriverCalibrationQualityEvidence(
                    1.2d,
                    null,
                    null,
                    0.94d),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)),
            Capture = new InternalDriverCaptureEvidence(
                24,
                1d,
                1d,
                0.9d,
                0.6d,
                210d,
                1d,
                1d,
                true,
                true),
        };
}
