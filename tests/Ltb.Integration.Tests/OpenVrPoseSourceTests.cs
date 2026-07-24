using System.Numerics;
using Ltb.Core;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class OpenVrPoseSourceTests
{
    [Fact]
    public void SourceCapturesHostIngressTimeAndPreservesAllMetadata()
    {
        var expectedPose = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.3f),
            new Vector3(1f, 2f, 3f));
        var expectedValidity =
            PoseValidity.Orientation | PoseValidity.TrackingValid;
        var runtimePose = new OpenVrRuntimePose(
            expectedPose,
            expectedValidity,
            IsConnected: false,
            PoseTrackingResult.RunningOutOfRange,
            RuntimeTimeSeconds: 41.25,
            PredictionOffsetSeconds: 0.025,
            SampleAgeSeconds: 0.003);
        using var runtime = new FakeOpenVrRuntime(pose: runtimePose);
        var clock = new AssertingClock(runtime, 1234.5);
        var source = new OpenVrInputControllerPoseSourceAdapter(
            runtime,
            clock,
            ControllerDescriptor(),
            predictionOffsetSeconds: 0.025);

        var sample = source.ReadPose();

        Assert.Equal(1234.5, sample.MonotonicHostTimeSeconds);
        Assert.Equal(expectedPose, sample.Pose);
        Assert.Equal(expectedValidity, sample.Validity);
        Assert.False(sample.IsConnected);
        Assert.Equal(PoseTrackingResult.RunningOutOfRange, sample.TrackingResult);
        Assert.Equal(41.25, sample.RuntimeTimeSeconds);
        Assert.Equal(0.025, sample.PredictionOffsetSeconds);
        Assert.Equal(0.003, sample.SampleAgeSeconds);
        Assert.Equal(0.025, runtime.LastPredictionOffsetSeconds);
        Assert.Equal(OpenVrTrackingUniverse.Standing, runtime.LastTrackingUniverse);

        var recorded = sample.ToRecordedPoseSample();
        Assert.Equal(sample.PoseSample, recorded.PoseSample);
        Assert.Equal(sample.IsConnected, recorded.IsConnected);
        Assert.Equal(sample.TrackingResult, recorded.TrackingResult);
        Assert.Equal(sample.RuntimeTimeSeconds, recorded.RuntimeTimeSeconds);
        Assert.Equal(sample.PredictionOffsetSeconds, recorded.PredictionOffsetSeconds);
        Assert.Equal(sample.SampleAgeSeconds, recorded.SampleAgeSeconds);
    }

    [Fact]
    public void SessionDefaultPoseFactoriesPreserveStandingUniverse()
    {
        using var runtime = new FakeOpenVrRuntime();
        using var session = new OpenVrSession(runtime);

        session.CreateInputControllerPoseSource(ControllerDescriptor()).ReadPose();
        Assert.Equal(OpenVrTrackingUniverse.Standing, runtime.LastTrackingUniverse);

        session.CreateTrackedPoseSource(TrackerDescriptor()).ReadPose();
        Assert.Equal(OpenVrTrackingUniverse.Standing, runtime.LastTrackingUniverse);
    }

    [Fact]
    public void SessionExplicitRawTrackerSourcePropagatesRawDriverSpaceUniverse()
    {
        using var runtime = new FakeOpenVrRuntime();
        using var session = new OpenVrSession(runtime);
        var source = session.CreateTrackedPoseSource(
            TrackerDescriptor(),
            OpenVrTrackingUniverse.RawAndUncalibrated);

        source.ReadPose();

        Assert.Equal(
            OpenVrTrackingUniverse.RawAndUncalibrated,
            runtime.LastTrackingUniverse);
    }

    [Fact]
    public void RawTrackerSourcePreservesExactIngressTimestampAndFiniteVelocities()
    {
        const double expectedIngressTime = 1234.567890123;
        var expectedLinearVelocity = new Vector3(1.25f, -2.5f, 0.75f);
        var expectedAngularVelocity = new Vector3(-0.5f, 1.5f, 2.25f);
        var runtimePose = new OpenVrRuntimePose(
            RigidTransform.Identity,
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid,
            IsConnected: true,
            PoseTrackingResult.RunningOk,
            RuntimeTimeSeconds: null,
            PredictionOffsetSeconds: 0d,
            SampleAgeSeconds: null,
            LinearVelocityMetersPerSecond: expectedLinearVelocity,
            AngularVelocityRadiansPerSecond: expectedAngularVelocity);
        using var runtime = new FakeOpenVrRuntime(pose: runtimePose);
        var source = new OpenVrTrackedPoseSourceAdapter(
            runtime,
            new AssertingClock(runtime, expectedIngressTime),
            TrackerDescriptor(),
            OpenVrTrackingUniverse.RawAndUncalibrated,
            predictionOffsetSeconds: 0d);

        var sample = source.ReadPose();

        Assert.Equal(expectedIngressTime, sample.MonotonicHostTimeSeconds);
        Assert.Equal(expectedLinearVelocity, sample.LinearVelocityMetersPerSecond);
        Assert.Equal(expectedAngularVelocity, sample.AngularVelocityRadiansPerSecond);
        Assert.Equal(
            OpenVrTrackingUniverse.RawAndUncalibrated,
            runtime.LastTrackingUniverse);
    }

    [Fact]
    public void BatchSourceUsesOneRuntimeAcquisitionAndOneSharedPostCallIngressTimestamp()
    {
        using var runtime = new BatchFakeOpenVrRuntime();
        var source = new OpenVrTrackedPoseBatchSourceAdapter(
            runtime,
            new BatchAssertingClock(runtime, 9876.54321),
            [
                TrackerDescriptor("tracker-left", 7),
                TrackerDescriptor("tracker-right", 11),
            ],
            OpenVrTrackingUniverse.RawAndUncalibrated,
            predictionOffsetSeconds: 0.0125);

        var samples = source.ReadPoses();

        Assert.Equal(1, runtime.BatchReadCount);
        Assert.Equal(new uint[] { 7, 11 }, runtime.LastTransientDeviceIndexes);
        Assert.Equal(OpenVrTrackingUniverse.RawAndUncalibrated, runtime.LastTrackingUniverse);
        Assert.Equal(0.0125, runtime.LastPredictionOffsetSeconds);
        Assert.Collection(
            samples,
            left =>
            {
                Assert.Equal("tracker-left", left.Device.StableDeviceId);
                Assert.Equal(9876.54321, left.Sample.MonotonicHostTimeSeconds);
                Assert.Equal(new Vector3(7f, 0f, 0f), left.Sample.Pose.TranslationMeters);
            },
            right =>
            {
                Assert.Equal("tracker-right", right.Device.StableDeviceId);
                Assert.Equal(9876.54321, right.Sample.MonotonicHostTimeSeconds);
                Assert.Equal(new Vector3(11f, 0f, 0f), right.Sample.Pose.TranslationMeters);
            });
    }

    [Fact]
    public void SessionBatchSourcePreservesReadPoseOnlyFakeCompatibility()
    {
        using var runtime = new FakeOpenVrRuntime();
        using var session = new OpenVrSession(runtime);
        var source = session.CreateTrackedPoseBatchSource(
            [
                TrackerDescriptor("tracker-left", 7),
                TrackerDescriptor("tracker-right", 11),
            ],
            OpenVrTrackingUniverse.RawAndUncalibrated);

        var samples = source.ReadPoses();

        Assert.Equal(2, runtime.ReadPoseCount);
        Assert.Equal(2, samples.Count);
        Assert.Equal(
            samples[0].Sample.MonotonicHostTimeSeconds,
            samples[1].Sample.MonotonicHostTimeSeconds);
    }

    [Fact]
    public void BatchSourceRejectsEmptyDuplicateAndOutOfRangeIndexesBeforeRuntimeRead()
    {
        using var runtime = new FakeOpenVrRuntime();
        var clock = new AssertingClock(runtime, 1d);

        Assert.Throws<ArgumentException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [],
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: 0d));
        Assert.Throws<ArgumentException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [
                    TrackerDescriptor("tracker-left", 7),
                    TrackerDescriptor("tracker-alias", 7),
                ],
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [TrackerDescriptor(
                    "tracker-out-of-range",
                    OpenVrRuntimePoseBatchValidation.MaximumTrackedDeviceCount)],
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: 0d));

        Assert.Equal(0, runtime.ReadPoseCount);
    }

    [Fact]
    public void BatchSourceRejectsInvalidUniverseAndPredictionBeforeRuntimeRead()
    {
        using var runtime = new FakeOpenVrRuntime();
        var device = TrackerDescriptor();
        var clock = new AssertingClock(runtime, 1d);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [device],
                (OpenVrTrackingUniverse)int.MaxValue,
                predictionOffsetSeconds: 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [device],
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpenVrTrackedPoseBatchSourceAdapter(
                runtime,
                clock,
                [device],
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: double.MaxValue));

        Assert.Equal(0, runtime.ReadPoseCount);
    }

    [Fact]
    public void NativeVelocityMappingPreservesFiniteVectorAndDropsInvalidVectors()
    {
        Assert.Equal(
            new Vector3(1.25f, -2.5f, 0.75f),
            ValveOpenVrRuntime.MapFiniteVelocity(1.25f, -2.5f, 0.75f));
        Assert.Null(ValveOpenVrRuntime.MapFiniteVelocity(float.NaN, 0f, 0f));
        Assert.Null(ValveOpenVrRuntime.MapFiniteVelocity(0f, float.PositiveInfinity, 0f));
        Assert.Null(ValveOpenVrRuntime.MapFiniteVelocity(0f, 0f, float.NegativeInfinity));
    }

    [Fact]
    public void PoseSourceSampleRejectsNonFiniteVelocityComponents()
    {
        var poseSample = new TimestampedPoseSample(
            1d,
            RigidTransform.Identity,
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);

        var linearException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PoseSourceSample(
                poseSample,
                true,
                PoseTrackingResult.RunningOk,
                runtimeTimeSeconds: null,
                predictionOffsetSeconds: null,
                sampleAgeSeconds: null,
                linearVelocityMetersPerSecond: new Vector3(float.NaN, 0f, 0f),
                angularVelocityRadiansPerSecond: null));
        var angularException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PoseSourceSample(
                poseSample,
                true,
                PoseTrackingResult.RunningOk,
                runtimeTimeSeconds: null,
                predictionOffsetSeconds: null,
                sampleAgeSeconds: null,
                linearVelocityMetersPerSecond: null,
                angularVelocityRadiansPerSecond: new Vector3(0f, float.PositiveInfinity, 0f)));

        Assert.Equal("linearVelocityMetersPerSecond", linearException.ParamName);
        Assert.Equal("angularVelocityRadiansPerSecond", angularException.ParamName);
    }

    [Fact]
    public void PoseFactoryRejectsUndefinedTrackingUniverse()
    {
        using var runtime = new FakeOpenVrRuntime();
        using var session = new OpenVrSession(runtime);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.CreateTrackedPoseSource(
                TrackerDescriptor(),
                (OpenVrTrackingUniverse)int.MaxValue));

        Assert.Equal("trackingUniverse", exception.ParamName);
        Assert.False(runtime.HasReadPose);
    }

    [Fact]
    public void SimulatedSourcesReplayExactlyAndStopAtEnd()
    {
        var samples = new[]
        {
            Sample(1d, Vector3.Zero),
            Sample(2d, Vector3.UnitX),
        };
        InputControllerPoseSource source = new SimulatedInputControllerPoseSource(
            ControllerDescriptor(),
            samples);

        Assert.Equal(samples[0], source.ReadPose());
        Assert.Equal(samples[1], source.ReadPose());
        Assert.Throws<InvalidOperationException>(() => source.ReadPose());
    }

    [Fact]
    public void MatrixConversionPreservesOpenVrTransformDirectionAndXyzwRotation()
    {
        // Column-vector OpenVR matrix for +90 degrees about +Z, with meters
        // in the final column.
        var matrix = new OpenVrMatrix34(
            0f, -1f, 0f, 1.25f,
            1f, 0f, 0f, -2.5f,
            0f, 0f, 1f, 0.75f);

        var converted = OpenVrMatrixConverter.TryConvert(matrix, out var transform);

        Assert.True(converted);
        AssertVectorClose(new Vector3(1.25f, -2.5f, 0.75f), transform.TranslationMeters);
        AssertVectorClose(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, transform.Rotation));
        Assert.True(transform.Rotation.W >= 0f);
    }

    [Fact]
    public void MatrixConversionRejectsMissingOrReflectedBasis()
    {
        Assert.False(OpenVrMatrixConverter.TryConvert(default, out _));

        var reflected = new OpenVrMatrix34(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, -1f, 0f);
        Assert.False(OpenVrMatrixConverter.TryConvert(reflected, out _));
    }

    [Fact]
    public void RotationOnlyFallbackNeverMarksPositionValid()
    {
        var rotationOnly = OpenVrPoseValidityMapper.Map(
            hasUsableMatrix: true,
            nativePoseIsValid: true,
            isRotationOnlyFallback: true);
        var fullPose = OpenVrPoseValidityMapper.Map(
            hasUsableMatrix: true,
            nativePoseIsValid: true,
            isRotationOnlyFallback: false);

        Assert.Equal(
            PoseValidity.Orientation | PoseValidity.TrackingValid,
            rotationOnly);
        Assert.Equal(
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid,
            fullPose);
        Assert.False(rotationOnly.HasFlag(PoseValidity.Position));
    }

    [Theory]
    [InlineData(
        (int)OpenVrTrackingResultCode.CalibratingInProgress,
        PoseTrackingResult.CalibratingInProgress)]
    [InlineData(
        (int)OpenVrTrackingResultCode.CalibratingOutOfRange,
        PoseTrackingResult.CalibratingOutOfRange)]
    [InlineData(
        (int)OpenVrTrackingResultCode.FallbackRotationOnly,
        PoseTrackingResult.FallbackRotationOnly)]
    public void TrackingResultMappingPreservesDistinctOpenVrStates(
        int nativeResultCode,
        PoseTrackingResult expected)
    {
        Assert.Equal(
            expected,
            ValveOpenVrRuntime.MapTrackingResult((OpenVrTrackingResultCode)nativeResultCode));
    }

    [Fact]
    public void PublicLtbOpenVrApiDoesNotExposeValveTypes()
    {
        var assembly = typeof(OpenVrSession).Assembly;
        var exportedTypes = assembly.GetExportedTypes();

        Assert.DoesNotContain(
            exportedTypes,
            type => type.Namespace?.StartsWith("Valve.VR", StringComparison.Ordinal) == true);

        var publicSignatureTypes = exportedTypes.SelectMany(type =>
            type.GetConstructors().SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
                .Concat(type.GetMethods().Select(method => method.ReturnType))
                .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)))
                .Concat(type.GetProperties().Select(property => property.PropertyType))
                .Concat(type.GetFields().Select(field => field.FieldType)));
        Assert.DoesNotContain(publicSignatureTypes, ContainsValveType);
    }

    [Fact]
    public void LegacyAndVelocityPoseSourceSampleConstructorsRemainAvailable()
    {
        Assert.NotNull(typeof(PoseSourceSample).GetConstructor(
        [
            typeof(TimestampedPoseSample),
            typeof(bool),
            typeof(PoseTrackingResult),
            typeof(double?),
            typeof(double?),
            typeof(double?),
        ]));
        Assert.NotNull(typeof(PoseSourceSample).GetConstructor(
        [
            typeof(TimestampedPoseSample),
            typeof(bool),
            typeof(PoseTrackingResult),
            typeof(double?),
            typeof(double?),
            typeof(double?),
            typeof(Vector3?),
            typeof(Vector3?),
        ]));
    }

    [Fact]
    public void NonWindowsFactoryFailureIsBoundedAndDoesNotLoadNativeRuntime()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<OpenVrUnavailableException>(OpenVrSession.Open);

        Assert.Equal(OpenVrUnavailableReason.UnsupportedPlatform, exception.Reason);
    }

    private static bool ContainsValveType(Type type)
    {
        if (type.Namespace?.StartsWith("Valve.VR", StringComparison.Ordinal) == true)
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsValveType);
    }

    private static PoseSourceSample Sample(double time, Vector3 position) =>
        new(
            new TimestampedPoseSample(
                time,
                new RigidTransform(Quaternion.Identity, position),
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
            true,
            PoseTrackingResult.RunningOk,
            runtimeTimeSeconds: time - 0.001,
            predictionOffsetSeconds: 0d,
            sampleAgeSeconds: 0.001);

    private static SteamVrDeviceDescriptor ControllerDescriptor() =>
        new(
            new SteamVrDeviceIdentity("touch-left", "oculus/touch-left"),
            5,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.LeftHand,
            true);

    private static SteamVrDeviceDescriptor TrackerDescriptor(
        string serialNumber = "tracker-left",
        uint transientDeviceIndex = 7) =>
        new(
            new SteamVrDeviceIdentity(serialNumber, $"lighthouse/{serialNumber}"),
            transientDeviceIndex,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-5f);
    }
}

internal sealed class FakeOpenVrRuntime : IOpenVrRuntime
{
    private readonly IReadOnlyList<OpenVrRuntimeDevice> _devices;
    private readonly OpenVrRuntimePose _pose;

    public FakeOpenVrRuntime(
        IReadOnlyList<OpenVrRuntimeDevice>? devices = null,
        OpenVrRuntimePose? pose = null)
    {
        _devices = devices ?? [];
        _pose = pose ?? new OpenVrRuntimePose(
            RigidTransform.Identity,
            PoseValidity.None,
            false,
            PoseTrackingResult.Unavailable,
            null,
            null,
            null);
    }

    public bool HasReadPose { get; private set; }

    public int ReadPoseCount { get; private set; }

    public double? LastPredictionOffsetSeconds { get; private set; }

    public OpenVrTrackingUniverse? LastTrackingUniverse { get; private set; }

    public IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices() => _devices;

    public OpenVrRuntimePose ReadPose(
        uint transientDeviceIndex,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        HasReadPose = true;
        ReadPoseCount++;
        LastTrackingUniverse = trackingUniverse;
        LastPredictionOffsetSeconds = predictionOffsetSeconds;
        return _pose;
    }

    public void Dispose()
    {
    }
}

internal sealed class BatchFakeOpenVrRuntime : IOpenVrRuntime
{
    public int BatchReadCount { get; private set; }

    public IReadOnlyList<uint> LastTransientDeviceIndexes { get; private set; } = [];

    public OpenVrTrackingUniverse? LastTrackingUniverse { get; private set; }

    public double? LastPredictionOffsetSeconds { get; private set; }

    public IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices() => [];

    public OpenVrRuntimePose ReadPose(
        uint transientDeviceIndex,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds) =>
        throw new InvalidOperationException("The optimized batch path called the single-pose fallback.");

    public IReadOnlyList<OpenVrRuntimePose> ReadPoses(
        IReadOnlyList<uint> transientDeviceIndexes,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        BatchReadCount++;
        LastTransientDeviceIndexes = transientDeviceIndexes.ToArray();
        LastTrackingUniverse = trackingUniverse;
        LastPredictionOffsetSeconds = predictionOffsetSeconds;
        return Array.AsReadOnly(transientDeviceIndexes
            .Select(index => new OpenVrRuntimePose(
                new RigidTransform(Quaternion.Identity, new Vector3(index, 0f, 0f)),
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid,
                IsConnected: true,
                PoseTrackingResult.RunningOk,
                RuntimeTimeSeconds: null,
                PredictionOffsetSeconds: predictionOffsetSeconds,
                SampleAgeSeconds: null))
            .ToArray());
    }

    public void Dispose()
    {
    }
}

internal sealed class AssertingClock : IMonotonicClock
{
    private readonly FakeOpenVrRuntime _runtime;
    private readonly double _timestamp;

    public AssertingClock(FakeOpenVrRuntime runtime, double timestamp)
    {
        _runtime = runtime;
        _timestamp = timestamp;
    }

    public double GetTimestampSeconds()
    {
        Assert.True(_runtime.HasReadPose, "Host time was captured before the runtime pose entered LTB.");
        return _timestamp;
    }
}

internal sealed class BatchAssertingClock : IMonotonicClock
{
    private readonly BatchFakeOpenVrRuntime _runtime;
    private readonly double _timestamp;

    public BatchAssertingClock(BatchFakeOpenVrRuntime runtime, double timestamp)
    {
        _runtime = runtime;
        _timestamp = timestamp;
    }

    public double GetTimestampSeconds()
    {
        Assert.Equal(1, _runtime.BatchReadCount);
        return _timestamp;
    }
}
