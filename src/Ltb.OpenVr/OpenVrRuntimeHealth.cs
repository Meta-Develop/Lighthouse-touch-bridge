namespace Ltb.OpenVr;

public enum OpenVrRuntimeHealthState
{
    Running,
    Stopped,
}

/// <summary>
/// Runtime-level health independent of device connectivity. A stopped state is
/// terminal for the current OpenVR session and must not be treated as a tracker
/// reconnect.
/// </summary>
public readonly record struct OpenVrRuntimeHealthSnapshot(
    OpenVrRuntimeHealthState State,
    string Diagnostic)
{
    public bool IsRunning => State == OpenVrRuntimeHealthState.Running;

    public static OpenVrRuntimeHealthSnapshot Running { get; } =
        new(OpenVrRuntimeHealthState.Running, "SteamVR runtime is running.");
}

internal enum OpenVrRuntimeEventDisposition
{
    Ignore,
    RuntimeStopped,
    RuntimeStoppedAndAcknowledgeQuit,
}

/// <summary>
/// Testable mapping for the small terminal-event subset without exposing Valve
/// types beyond the native adapter. A process-quit event concerns another
/// OpenVR client and is deliberately not a SteamVR-stop signal.
/// </summary>
internal static class OpenVrRuntimeEventSemantics
{
    internal const uint RuntimeQuitEvent = 700;
    internal const uint ProcessQuitEvent = 701;
    internal const uint DriverRequestedQuitEvent = 704;

    public static OpenVrRuntimeEventDisposition Classify(uint eventType) => eventType switch
    {
        RuntimeQuitEvent => OpenVrRuntimeEventDisposition.RuntimeStoppedAndAcknowledgeQuit,
        DriverRequestedQuitEvent => OpenVrRuntimeEventDisposition.RuntimeStopped,
        _ => OpenVrRuntimeEventDisposition.Ignore,
    };
}
