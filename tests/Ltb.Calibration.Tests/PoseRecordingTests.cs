using System.Numerics;
using System.Text.Json;
using Ltb.Core;

namespace Ltb.Calibration.Tests;

public sealed class PoseRecordingTests
{
    [Fact]
    public void VersionOneJsonRoundTripIsDeterministicAndPreservesMetadata()
    {
        var recording = CreateRecording();

        var firstJson = PoseRecordingJson.Serialize(recording);
        var replayed = PoseRecordingJson.Deserialize(firstJson);
        var secondJson = PoseRecordingJson.Serialize(replayed);

        Assert.Equal(firstJson, secondJson);
        Assert.Contains("\"format\": \"ltb-pose-recording\"", firstJson);
        Assert.Contains("\"schemaVersion\": 1", firstJson);

        using var document = JsonDocument.Parse(firstJson);
        var firstSample = document.RootElement
            .GetProperty("streams")[0]
            .GetProperty("samples")[0];
        var expectedRotation = Quaternion.Normalize(new Quaternion(0.1f, -0.2f, 0.3f, 0.9f));
        Assert.Equal(
            new[] { expectedRotation.X, expectedRotation.Y, expectedRotation.Z, expectedRotation.W },
            firstSample.GetProperty("orientationXyzw")
                .EnumerateArray()
                .Select(value => value.GetSingle())
                .ToArray());
        Assert.Equal(
            new[] { 1.25f, -0.5f, 0.125f },
            firstSample.GetProperty("translationMeters")
                .EnumerateArray()
                .Select(value => value.GetSingle())
                .ToArray());

        var stream = Assert.Single(replayed.Streams);
        Assert.Equal("touch-left", stream.Identity.StreamId);
        Assert.Equal(PoseSourceKind.InputController, stream.Identity.SourceKind);
        Assert.Equal("synthetic-touch-left", stream.Identity.DeviceId);
        Assert.Equal("Synthetic Touch Left", stream.Identity.DisplayName);
        var sample = stream.Samples[0];
        Assert.True(sample.IsConnected);
        Assert.Equal(PoseTrackingResult.RunningOk, sample.TrackingResult);
        Assert.Equal(10.5, sample.RuntimeTimeSeconds);
        Assert.Equal(0.011, sample.PredictionOffsetSeconds);
        Assert.Equal(0.002, sample.SampleAgeSeconds);
        Assert.True(sample.PoseSample.HasFullValidPose);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejectedExplicitly()
    {
        var json = PoseRecordingJson.Serialize(CreateRecording())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<NotSupportedException>(() =>
            PoseRecordingJson.Deserialize(json));

        Assert.Contains("schema version 2", exception.Message);
    }

    [Fact]
    public void MalformedAndNonMonotonicRecordingsAreRejected()
    {
        Assert.Throws<PoseRecordingFormatException>(() =>
            PoseRecordingJson.Deserialize("{\"format\":\"ltb-pose-recording\",\"schemaVersion\":1}"));

        var json = PoseRecordingJson.Serialize(CreateRecording())
            .Replace(
                "\"monotonicHostTimeSeconds\": 1.25",
                "\"monotonicHostTimeSeconds\": 0.5",
                StringComparison.Ordinal);
        var exception = Assert.Throws<PoseRecordingFormatException>(() =>
            PoseRecordingJson.Deserialize(json));
        Assert.Contains("malformed or violates", exception.Message);
    }

    [Fact]
    public void PoseStreamBufferRejectsOutOfOrderAppendAndSnapshotsByValue()
    {
        var identity = new PoseStreamIdentity(
            "tracker-1",
            PoseSourceKind.TrackedPose,
            "synthetic-tracker-1");
        var buffer = new PoseStreamBuffer(identity);
        buffer.Append(CreateSample(1.0, PoseValidity.Orientation));
        var snapshot = buffer.Snapshot();
        buffer.Append(CreateSample(2.0, PoseValidity.Orientation | PoseValidity.Position));

        Assert.Single(snapshot.Samples);
        Assert.Equal(2, buffer.Count);
        Assert.Throws<ArgumentException>(() =>
            buffer.Append(CreateSample(1.5, PoseValidity.Orientation)));
        Assert.Throws<ArgumentException>(() => buffer.Append(default));
    }

    [Theory]
    [InlineData(PoseTrackingResult.CalibratingInProgress, "calibratingInProgress")]
    [InlineData(PoseTrackingResult.CalibratingOutOfRange, "calibratingOutOfRange")]
    [InlineData(PoseTrackingResult.FallbackRotationOnly, "fallbackRotationOnly")]
    public void DistinctTrackingResultsHaveStableJsonSpellings(
        PoseTrackingResult trackingResult,
        string expectedSpelling)
    {
        var recording = new PoseRecording(
        [
            new PoseStreamRecording(
                new PoseStreamIdentity("tracker", PoseSourceKind.TrackedPose, "synthetic-tracker"),
                [
                    new RecordedPoseSample(
                        new TimestampedPoseSample(
                            1d,
                            RigidTransform.Identity,
                            PoseValidity.Orientation | PoseValidity.TrackingValid),
                        true,
                        trackingResult),
                ]),
        ]);

        var json = PoseRecordingJson.Serialize(recording);
        var roundTripped = PoseRecordingJson.Deserialize(json);

        Assert.Contains($"\"trackingResult\": \"{expectedSpelling}\"", json);
        Assert.Equal(trackingResult, roundTripped.Streams[0].Samples[0].TrackingResult);
    }

    [Theory]
    [InlineData("\"format\": \"ltb-pose-recording\"", "recording root")]
    [InlineData("\"streamId\": \"touch-left\"", "stream")]
    [InlineData("\"connected\": true", "sample")]
    [InlineData("\"tracking\": true", "sample validity")]
    public void DuplicatePropertiesAreRejectedAtEverySchemaObject(
        string propertyText,
        string expectedObjectDescription)
    {
        var json = PoseRecordingJson.Serialize(CreateRecording())
            .Replace(propertyText, $"{propertyText},\n    {propertyText}", StringComparison.Ordinal);

        var exception = Assert.Throws<PoseRecordingFormatException>(() =>
            PoseRecordingJson.Deserialize(json));

        Assert.Contains(expectedObjectDescription, exception.Message);
        Assert.Contains("duplicate property", exception.Message);
    }

    private static PoseRecording CreateRecording()
    {
        var normalizedRotation = Quaternion.Normalize(new Quaternion(0.1f, -0.2f, 0.3f, 0.9f));
        var samples = new[]
        {
            new RecordedPoseSample(
                new TimestampedPoseSample(
                    1.0,
                    new RigidTransform(normalizedRotation, new Vector3(1.25f, -0.5f, 0.125f)),
                    PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
                isConnected: true,
                PoseTrackingResult.RunningOk,
                runtimeTimeSeconds: 10.5,
                predictionOffsetSeconds: 0.011,
                sampleAgeSeconds: 0.002),
            new RecordedPoseSample(
                new TimestampedPoseSample(
                    1.25,
                    new RigidTransform(Quaternion.Identity, new Vector3(1.5f, -0.4f, 0.2f)),
                    PoseValidity.Orientation),
                isConnected: false,
                PoseTrackingResult.Unavailable),
        };
        return new PoseRecording(
        [
            new PoseStreamRecording(
                new PoseStreamIdentity(
                    "touch-left",
                    PoseSourceKind.InputController,
                    "synthetic-touch-left",
                    "Synthetic Touch Left"),
                samples),
        ]);
    }

    private static RecordedPoseSample CreateSample(double time, PoseValidity channels) =>
        new(
            new TimestampedPoseSample(
                time,
                RigidTransform.Identity,
                channels | PoseValidity.TrackingValid),
            true,
            PoseTrackingResult.RunningOk);
}
