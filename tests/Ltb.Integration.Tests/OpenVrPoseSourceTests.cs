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

        var recorded = sample.ToRecordedPoseSample();
        Assert.Equal(sample.PoseSample, recorded.PoseSample);
        Assert.Equal(sample.IsConnected, recorded.IsConnected);
        Assert.Equal(sample.TrackingResult, recorded.TrackingResult);
        Assert.Equal(sample.RuntimeTimeSeconds, recorded.RuntimeTimeSeconds);
        Assert.Equal(sample.PredictionOffsetSeconds, recorded.PredictionOffsetSeconds);
        Assert.Equal(sample.SampleAgeSeconds, recorded.SampleAgeSeconds);
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

    public double? LastPredictionOffsetSeconds { get; private set; }

    public IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices() => _devices;

    public OpenVrRuntimePose ReadPose(uint transientDeviceIndex, double predictionOffsetSeconds)
    {
        HasReadPose = true;
        LastPredictionOffsetSeconds = predictionOffsetSeconds;
        return _pose;
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
