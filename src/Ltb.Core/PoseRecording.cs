using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Ltb.Core;

/// <summary>Runtime-neutral kind of pose source represented by a recorded stream.</summary>
public enum PoseSourceKind
{
    InputController,
    TrackedPose,
}

/// <summary>
/// Runtime-neutral tracking state retained with each sample. Runtime adapters
/// map their native status values to this contract without leaking interop types.
/// </summary>
public enum PoseTrackingResult
{
    Unknown,
    Uninitialized,
    CalibratingInProgress,
    CalibratingOutOfRange,
    RunningOk,
    RunningOutOfRange,
    FallbackRotationOnly,
    Unavailable,
}

/// <summary>Stable stream and device identity stored in a recording.</summary>
public sealed record PoseStreamIdentity
{
    public PoseStreamIdentity(
        string streamId,
        PoseSourceKind sourceKind,
        string deviceId,
        string? displayName = null)
    {
        StreamId = RequireIdentifier(streamId, nameof(streamId));
        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        SourceKind = sourceKind;
        DeviceId = RequireIdentifier(deviceId, nameof(deviceId));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    public string StreamId { get; }

    public PoseSourceKind SourceKind { get; }

    /// <summary>
    /// Source-provided stable identity, such as a synthetic fixture identifier
    /// or a runtime serial. Callers remain responsible for recording privacy.
    /// </summary>
    public string DeviceId { get; }

    public string? DisplayName { get; }

    private static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>
/// One recorded runtime-neutral pose sample. Host time and all optional time
/// metadata use seconds. The pose uses an XYZW quaternion and meter translation.
/// </summary>
public readonly record struct RecordedPoseSample
{
    public RecordedPoseSample(
        TimestampedPoseSample poseSample,
        bool isConnected,
        PoseTrackingResult trackingResult,
        double? runtimeTimeSeconds = null,
        double? predictionOffsetSeconds = null,
        double? sampleAgeSeconds = null)
    {
        if (!poseSample.Pose.IsValid)
        {
            throw new ArgumentException("Recorded pose sample must contain a valid pose.", nameof(poseSample));
        }

        if (!Enum.IsDefined(trackingResult))
        {
            throw new ArgumentOutOfRangeException(nameof(trackingResult));
        }

        RequireFiniteOptional(runtimeTimeSeconds, nameof(runtimeTimeSeconds));
        RequireFiniteOptional(predictionOffsetSeconds, nameof(predictionOffsetSeconds));
        if (sampleAgeSeconds is { } age && (!double.IsFinite(age) || age < 0d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleAgeSeconds),
                "Sample age must be finite and non-negative when present.");
        }

        PoseSample = poseSample;
        IsConnected = isConnected;
        TrackingResult = trackingResult;
        RuntimeTimeSeconds = runtimeTimeSeconds;
        PredictionOffsetSeconds = predictionOffsetSeconds;
        SampleAgeSeconds = sampleAgeSeconds;
    }

    public TimestampedPoseSample PoseSample { get; }

    public double MonotonicHostTimeSeconds => PoseSample.MonotonicTimeSeconds;

    public RigidTransform Pose => PoseSample.Pose;

    public PoseValidity Validity => PoseSample.Validity;

    public bool IsConnected { get; }

    public PoseTrackingResult TrackingResult { get; }

    /// <summary>Runtime-provided pose time when the source exposes one.</summary>
    public double? RuntimeTimeSeconds { get; }

    /// <summary>Runtime-provided prediction offset; positive means predicted ahead.</summary>
    public double? PredictionOffsetSeconds { get; }

    /// <summary>Age of the runtime sample at host ingestion time.</summary>
    public double? SampleAgeSeconds { get; }

    private static void RequireFiniteOptional(double? value, string parameterName)
    {
        if (value is { } present && !double.IsFinite(present))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite when present.");
        }
    }
}

/// <summary>One strictly monotonic device pose stream.</summary>
public sealed class PoseStreamRecording
{
    private readonly IReadOnlyList<RecordedPoseSample> _samples;

    public PoseStreamRecording(
        PoseStreamIdentity identity,
        IEnumerable<RecordedPoseSample> samples)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentNullException.ThrowIfNull(samples);

        var snapshot = samples.ToArray();
        var previousTimestamp = double.NegativeInfinity;
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (!snapshot[index].Pose.IsValid)
            {
                throw new ArgumentException(
                    $"Stream '{identity.StreamId}' sample {index} does not contain a valid pose.",
                    nameof(samples));
            }

            var timestamp = snapshot[index].MonotonicHostTimeSeconds;
            if (timestamp <= previousTimestamp)
            {
                throw new ArgumentException(
                    $"Stream '{identity.StreamId}' host timestamps must increase strictly; sample {index} has {timestamp:R} after {previousTimestamp:R}.",
                    nameof(samples));
            }

            previousTimestamp = timestamp;
        }

        _samples = Array.AsReadOnly(snapshot);
    }

    public PoseStreamIdentity Identity { get; }

    public IReadOnlyList<RecordedPoseSample> Samples => _samples;
}

/// <summary>Versioned collection of replayable pose streams.</summary>
public sealed class PoseRecording
{
    public const string FormatIdentifier = "ltb-pose-recording";

    public const int CurrentSchemaVersion = 1;

    private readonly IReadOnlyList<PoseStreamRecording> _streams;

    public PoseRecording(
        IEnumerable<PoseStreamRecording> streams,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Recording schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
        }

        ArgumentNullException.ThrowIfNull(streams);
        var snapshot = streams.ToArray();
        if (snapshot.Any(stream => stream is null))
        {
            throw new ArgumentException("Recording streams cannot contain null entries.", nameof(streams));
        }

        var duplicateStreamId = snapshot
            .GroupBy(stream => stream.Identity.StreamId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateStreamId is not null)
        {
            throw new ArgumentException(
                $"Recording contains duplicate stream id '{duplicateStreamId}'.",
                nameof(streams));
        }

        SchemaVersion = schemaVersion;
        _streams = Array.AsReadOnly(snapshot);
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<PoseStreamRecording> Streams => _streams;

    public PoseStreamRecording GetStream(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        return _streams.SingleOrDefault(
                stream => string.Equals(stream.Identity.StreamId, streamId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Recording does not contain stream '{streamId}'.");
    }
}

/// <summary>Raised when recording JSON does not satisfy the versioned schema.</summary>
public sealed class PoseRecordingFormatException : FormatException
{
    public PoseRecordingFormatException(string message)
        : base(message)
    {
    }

    public PoseRecordingFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Deterministic JSON reader/writer for <see cref="PoseRecording"/> schema 1.
/// Property order and enum spellings are part of the persisted contract.
/// </summary>
public static class PoseRecordingJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
    };

    public static string Serialize(PoseRecording recording) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(recording));

    public static byte[] SerializeToUtf8Bytes(PoseRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        using var buffer = new MemoryStream();
        Write(recording, buffer);
        return buffer.ToArray();
    }

    public static void Write(PoseRecording recording, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        using var writer = new Utf8JsonWriter(destination, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("format", PoseRecording.FormatIdentifier);
        writer.WriteNumber("schemaVersion", recording.SchemaVersion);
        writer.WriteStartArray("streams");
        foreach (var stream in recording.Streams)
        {
            WriteStream(writer, stream);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    public static PoseRecording Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Deserialize(Encoding.UTF8.GetBytes(json));
    }

    public static PoseRecording Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = RequireObject(document.RootElement, "recording root");
            var format = RequireString(root, "format");
            if (!string.Equals(format, PoseRecording.FormatIdentifier, StringComparison.Ordinal))
            {
                throw new PoseRecordingFormatException(
                    $"Unsupported recording format '{format}'.");
            }

            var schemaVersion = RequireInt32(root, "schemaVersion");
            if (schemaVersion != PoseRecording.CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"Recording schema version {schemaVersion} is not supported; expected {PoseRecording.CurrentSchemaVersion}.");
            }

            var streamElements = RequireArray(root, "streams");
            var streams = new List<PoseStreamRecording>(streamElements.GetArrayLength());
            foreach (var streamElement in streamElements.EnumerateArray())
            {
                streams.Add(ReadStream(RequireObject(streamElement, "stream")));
            }

            return new PoseRecording(streams, schemaVersion);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (PoseRecordingFormatException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new PoseRecordingFormatException(
                "Recording JSON is malformed or violates the schema.",
                exception);
        }
    }

    public static PoseRecording Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return Deserialize(buffer.ToArray());
    }

    private static void WriteStream(Utf8JsonWriter writer, PoseStreamRecording stream)
    {
        writer.WriteStartObject();
        writer.WriteString("streamId", stream.Identity.StreamId);
        writer.WriteString("sourceKind", FormatSourceKind(stream.Identity.SourceKind));
        writer.WriteString("deviceId", stream.Identity.DeviceId);
        if (stream.Identity.DisplayName is { } displayName)
        {
            writer.WriteString("displayName", displayName);
        }

        writer.WriteStartArray("samples");
        foreach (var sample in stream.Samples)
        {
            writer.WriteStartObject();
            writer.WriteNumber("monotonicHostTimeSeconds", sample.MonotonicHostTimeSeconds);
            WriteNullableNumber(writer, "runtimeTimeSeconds", sample.RuntimeTimeSeconds);
            WriteNullableNumber(writer, "predictionOffsetSeconds", sample.PredictionOffsetSeconds);
            WriteNullableNumber(writer, "sampleAgeSeconds", sample.SampleAgeSeconds);
            writer.WriteBoolean("connected", sample.IsConnected);
            writer.WriteString("trackingResult", FormatTrackingResult(sample.TrackingResult));
            writer.WriteStartObject("validity");
            writer.WriteBoolean("orientation", sample.Validity.HasFlag(PoseValidity.Orientation));
            writer.WriteBoolean("position", sample.Validity.HasFlag(PoseValidity.Position));
            writer.WriteBoolean("tracking", sample.Validity.HasFlag(PoseValidity.TrackingValid));
            writer.WriteEndObject();
            WriteQuaternion(writer, sample.Pose.Rotation);
            WriteTranslation(writer, sample.Pose.TranslationMeters);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static PoseStreamRecording ReadStream(JsonElement element)
    {
        var identity = new PoseStreamIdentity(
            RequireString(element, "streamId"),
            ParseSourceKind(RequireString(element, "sourceKind")),
            RequireString(element, "deviceId"),
            OptionalString(element, "displayName"));
        var sampleElements = RequireArray(element, "samples");
        var samples = new List<RecordedPoseSample>(sampleElements.GetArrayLength());
        foreach (var sampleElement in sampleElements.EnumerateArray())
        {
            samples.Add(ReadSample(RequireObject(sampleElement, "sample")));
        }

        return new PoseStreamRecording(identity, samples);
    }

    private static RecordedPoseSample ReadSample(JsonElement element)
    {
        var validityElement = RequireObject(RequireProperty(element, "validity"), "sample validity");
        var validity = PoseValidity.None;
        if (RequireBoolean(validityElement, "orientation"))
        {
            validity |= PoseValidity.Orientation;
        }

        if (RequireBoolean(validityElement, "position"))
        {
            validity |= PoseValidity.Position;
        }

        if (RequireBoolean(validityElement, "tracking"))
        {
            validity |= PoseValidity.TrackingValid;
        }

        var rotationValues = ReadNumberArray(element, "orientationXyzw", 4);
        var translationValues = ReadNumberArray(element, "translationMeters", 3);
        var pose = new RigidTransform(
            new Quaternion(
                (float)rotationValues[0],
                (float)rotationValues[1],
                (float)rotationValues[2],
                (float)rotationValues[3]),
            new Vector3(
                (float)translationValues[0],
                (float)translationValues[1],
                (float)translationValues[2]));
        var timestamped = new TimestampedPoseSample(
            RequireFiniteDouble(element, "monotonicHostTimeSeconds"),
            pose,
            validity);
        return new RecordedPoseSample(
            timestamped,
            RequireBoolean(element, "connected"),
            ParseTrackingResult(RequireString(element, "trackingResult")),
            OptionalFiniteDouble(element, "runtimeTimeSeconds"),
            OptionalFiniteDouble(element, "predictionOffsetSeconds"),
            OptionalFiniteDouble(element, "sampleAgeSeconds"));
    }

    private static void WriteQuaternion(Utf8JsonWriter writer, Quaternion rotation)
    {
        writer.WriteStartArray("orientationXyzw");
        writer.WriteNumberValue(rotation.X);
        writer.WriteNumberValue(rotation.Y);
        writer.WriteNumberValue(rotation.Z);
        writer.WriteNumberValue(rotation.W);
        writer.WriteEndArray();
    }

    private static void WriteTranslation(Utf8JsonWriter writer, Vector3 translation)
    {
        writer.WriteStartArray("translationMeters");
        writer.WriteNumberValue(translation.X);
        writer.WriteNumberValue(translation.Y);
        writer.WriteNumberValue(translation.Z);
        writer.WriteEndArray();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(propertyName, present);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? value
            : throw new PoseRecordingFormatException($"Required property '{propertyName}' is missing.");

    private static JsonElement RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PoseRecordingFormatException($"{description} must be a JSON object.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new PoseRecordingFormatException(
                    $"{description} contains duplicate property '{property.Name}'.");
            }
        }

        return element;
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new PoseRecordingFormatException($"Property '{propertyName}' must be an array.");
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new PoseRecordingFormatException(
                $"Property '{propertyName}' must be a non-empty string.");
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new PoseRecordingFormatException($"Property '{propertyName}' must be a string or null.");
    }

    private static bool RequireBoolean(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new PoseRecordingFormatException($"Property '{propertyName}' must be a boolean.");
    }

    private static int RequireInt32(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new PoseRecordingFormatException($"Property '{propertyName}' must be a 32-bit integer.");
    }

    private static double RequireFiniteDouble(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw new PoseRecordingFormatException($"Property '{propertyName}' must be a finite number.");
        }

        return result;
    }

    private static double? OptionalFiniteDouble(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw new PoseRecordingFormatException($"Property '{propertyName}' must be a finite number or null.");
        }

        return result;
    }

    private static double[] ReadNumberArray(JsonElement element, string propertyName, int count)
    {
        var array = RequireArray(element, propertyName);
        if (array.GetArrayLength() != count)
        {
            throw new PoseRecordingFormatException(
                $"Property '{propertyName}' must contain exactly {count} numbers.");
        }

        var result = new double[count];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number ||
                !item.TryGetDouble(out var value) ||
                !double.IsFinite(value))
            {
                throw new PoseRecordingFormatException(
                    $"Property '{propertyName}' must contain only finite numbers.");
            }

            result[index++] = value;
        }

        return result;
    }

    private static string FormatSourceKind(PoseSourceKind value) => value switch
    {
        PoseSourceKind.InputController => "inputController",
        PoseSourceKind.TrackedPose => "trackedPose",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PoseSourceKind ParseSourceKind(string value) => value switch
    {
        "inputController" => PoseSourceKind.InputController,
        "trackedPose" => PoseSourceKind.TrackedPose,
        _ => throw new PoseRecordingFormatException($"Unknown pose source kind '{value}'."),
    };

    private static string FormatTrackingResult(PoseTrackingResult value) => value switch
    {
        PoseTrackingResult.Unknown => "unknown",
        PoseTrackingResult.Uninitialized => "uninitialized",
        PoseTrackingResult.CalibratingInProgress => "calibratingInProgress",
        PoseTrackingResult.CalibratingOutOfRange => "calibratingOutOfRange",
        PoseTrackingResult.RunningOk => "runningOk",
        PoseTrackingResult.RunningOutOfRange => "runningOutOfRange",
        PoseTrackingResult.FallbackRotationOnly => "fallbackRotationOnly",
        PoseTrackingResult.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PoseTrackingResult ParseTrackingResult(string value) => value switch
    {
        "unknown" => PoseTrackingResult.Unknown,
        "uninitialized" => PoseTrackingResult.Uninitialized,
        "calibratingInProgress" => PoseTrackingResult.CalibratingInProgress,
        "calibratingOutOfRange" => PoseTrackingResult.CalibratingOutOfRange,
        "runningOk" => PoseTrackingResult.RunningOk,
        "runningOutOfRange" => PoseTrackingResult.RunningOutOfRange,
        "fallbackRotationOnly" => PoseTrackingResult.FallbackRotationOnly,
        "unavailable" => PoseTrackingResult.Unavailable,
        _ => throw new PoseRecordingFormatException($"Unknown tracking result '{value}'."),
    };
}
