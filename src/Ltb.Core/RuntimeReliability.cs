using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ltb.Core;

/// <summary>The complete application state vocabulary from specification section 18.</summary>
public enum RuntimeApplicationState
{
    Stopped,
    DependencyCheck,
    WaitingForSteamVR,
    WaitingForDevices,
    Ready,
    OverrideRelease,
    Recording,
    Association,
    TimeAlignment,
    RotationSolve,
    TranslationAttempt,
    Validation,
    ApplyProfile,
    Active,
    SafeDisable,
}

/// <summary>Stable severity used by local structured log sinks.</summary>
public enum LtbLogLevel
{
    Trace,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// Stable event and diagnostic codes. These values are intended for tests and
/// support tooling; user-facing wording may evolve without changing the code.
/// </summary>
public enum LtbDiagnosticCode
{
    StateTransition,
    DependencyUnavailable,
    DevicesUnavailable,
    ProfileUnavailable,
    ProfileApplied,
    NoPositionAvailable,
    PoorTranslationObservability,
    BadRotationCalibration,
    TrackerLost,
    TouchInputLost,
    VmtUnavailable,
    SteamVrStopped,
    ProfileApplyFailed,
    SafeDisableStarted,
    SafeDisableCompleted,
    SafeDisableFailed,
    RollbackCompleted,
    RollbackFailed,
    ReconnectWaiting,
    Reconnected,
    ShutdownRequested,
    RuntimeFailure,
}

/// <summary>Typed reason returned by the active runtime health boundary.</summary>
public enum RuntimeHealthFailureKind
{
    None,
    TrackerLost,
    TouchInputLost,
    VmtUnavailable,
    SteamVrStopped,
}

/// <summary>
/// One health observation. Device identity is a stable serial when available;
/// no transient OpenVR index belongs in this portable contract.
/// </summary>
public sealed record RuntimeHealthSnapshot
{
    public RuntimeHealthSnapshot(
        RuntimeHealthFailureKind failureKind,
        string diagnostic,
        string? hand = null,
        string? deviceIdentity = null)
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        FailureKind = failureKind;
        Diagnostic = diagnostic;
        Hand = NormalizeOptional(hand, nameof(hand));
        DeviceIdentity = NormalizeOptional(deviceIdentity, nameof(deviceIdentity));
    }

    public RuntimeHealthFailureKind FailureKind { get; }

    public string Diagnostic { get; }

    public string? Hand { get; }

    public string? DeviceIdentity { get; }

    public bool IsHealthy => FailureKind == RuntimeHealthFailureKind.None;

    public static RuntimeHealthSnapshot Healthy(string diagnostic = "runtime health checks passed") =>
        new(RuntimeHealthFailureKind.None, diagnostic);

    private static string? NormalizeOptional(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>Immutable local structured-log event.</summary>
public sealed record LtbLogEvent
{
    public LtbLogEvent(
        DateTimeOffset timestampUtc,
        LtbLogLevel level,
        LtbDiagnosticCode code,
        RuntimeApplicationState state,
        string message,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        if (timestampUtc == default || timestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Structured log timestamps must be non-default UTC values.",
                nameof(timestampUtc));
        }

        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        TimestampUtc = timestampUtc;
        Level = level;
        Code = code;
        State = state;
        Message = message;
        Properties = FreezeProperties(properties);
    }

    public DateTimeOffset TimestampUtc { get; }

    public LtbLogLevel Level { get; }

    public LtbDiagnosticCode Code { get; }

    public RuntimeApplicationState State { get; }

    public string Message { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    private static IReadOnlyDictionary<string, string> FreezeProperties(
        IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return EmptyProperties.Instance;
        }

        var copy = new Dictionary<string, string>(properties.Count, StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key, nameof(properties));
            ArgumentNullException.ThrowIfNull(pair.Value, nameof(properties));
            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"Structured log property '{pair.Key}' appears more than once.",
                    nameof(properties));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static class EmptyProperties
    {
        public static IReadOnlyDictionary<string, string> Instance { get; } =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
    }
}

/// <summary>Destination for local structured runtime events.</summary>
public interface ILtbLogSink
{
    void Write(LtbLogEvent logEvent);
}

/// <summary>A no-op sink for callers that do not request durable logs.</summary>
public sealed class NullLtbLogSink : ILtbLogSink
{
    public static NullLtbLogSink Instance { get; } = new();

    private NullLtbLogSink()
    {
    }

    public void Write(LtbLogEvent logEvent) => ArgumentNullException.ThrowIfNull(logEvent);
}

/// <summary>Thread-safe in-memory event sink for deterministic tests and adapters.</summary>
public sealed class InMemoryLtbLogSink : ILtbLogSink
{
    private readonly object _sync = new();
    private readonly List<LtbLogEvent> _events = [];

    public IReadOnlyList<LtbLogEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public void Write(LtbLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_sync)
        {
            _events.Add(logEvent);
        }
    }
}

/// <summary>
/// Writes one self-contained JSON object per line. The sink is local-only and
/// performs no network, telemetry, or cloud operations.
/// </summary>
public sealed class JsonLinesLtbLogSink : ILtbLogSink, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();
    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private bool _disposed;

    public JsonLinesLtbLogSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Log path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        _writer = new StreamWriter(
            new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false);
    }

    public JsonLinesLtbLogSink(TextWriter writer, bool leaveOpen = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _leaveOpen = leaveOpen;
    }

    public void Write(LtbLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(JsonSerializer.Serialize(logEvent, SerializerOptions));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_leaveOpen)
            {
                _writer.Dispose();
            }

            _disposed = true;
        }
    }
}
