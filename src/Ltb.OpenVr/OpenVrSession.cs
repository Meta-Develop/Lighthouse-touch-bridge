namespace Ltb.OpenVr;

/// <summary>
/// Explicitly owned connection to the live Windows OpenVR runtime. Constructing
/// or reflecting over other LTB types never initializes the native runtime.
/// </summary>
public sealed class OpenVrSession : SteamVrDeviceEnumerator, IDisposable
{
    private readonly IOpenVrRuntime _runtime;
    private readonly OpenVrDeviceEnumeratorAdapter _deviceEnumerator;
    private bool _disposed;

    internal OpenVrSession(IOpenVrRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _deviceEnumerator = new OpenVrDeviceEnumeratorAdapter(runtime);
    }

    /// <summary>Connects as an OpenVR background application.</summary>
    /// <exception cref="OpenVrUnavailableException">
    /// Thrown with a bounded reason when the platform, native binding, or
    /// SteamVR runtime is unavailable.
    /// </exception>
    public static OpenVrSession Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new OpenVrUnavailableException(
                OpenVrUnavailableReason.UnsupportedPlatform,
                "The live Lighthouse Touch Bridge OpenVR adapter is supported on Windows only.");
        }

        try
        {
            return new OpenVrSession(ValveOpenVrRuntime.Open());
        }
        catch (DllNotFoundException exception)
        {
            throw new OpenVrUnavailableException(
                OpenVrUnavailableReason.NativeLibraryUnavailable,
                "The OpenVR native library was not found. Install or repair SteamVR, then retry.",
                exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new OpenVrUnavailableException(
                OpenVrUnavailableReason.NativeEntryPointUnavailable,
                "The installed OpenVR native library does not expose the required API entry points.",
                exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new OpenVrUnavailableException(
                OpenVrUnavailableReason.NativeLibraryArchitectureMismatch,
                "The OpenVR native library architecture does not match this process.",
                exception);
        }
        catch (OpenVrRuntimeInitializationException exception)
        {
            throw new OpenVrUnavailableException(
                OpenVrUnavailableReason.RuntimeInitializationFailed,
                $"SteamVR rejected the OpenVR background connection: {exception.Message}",
                exception);
        }
    }

    public IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices()
    {
        ThrowIfDisposed();
        return _deviceEnumerator.EnumerateDevices();
    }

    /// <summary>
    /// Polls the current OpenVR session for a terminal runtime-quit event.
    /// Device disconnects remain represented by device descriptors and poses.
    /// </summary>
    public OpenVrRuntimeHealthSnapshot GetRuntimeHealth()
    {
        ThrowIfDisposed();
        return _runtime.GetRuntimeHealth();
    }

    public InputControllerPoseSource CreateInputControllerPoseSource(
        SteamVrDeviceDescriptor device,
        double predictionOffsetSeconds = 0d)
    {
        return CreateInputControllerPoseSource(
            device,
            OpenVrTrackingUniverse.Standing,
            predictionOffsetSeconds);
    }

    /// <summary>
    /// Creates an input-controller source in the explicitly selected OpenVR
    /// coordinate frame. Use <see cref="OpenVrTrackingUniverse.RawAndUncalibrated"/>
    /// only when the consumer expects raw driver-space poses.
    /// </summary>
    public InputControllerPoseSource CreateInputControllerPoseSource(
        SteamVrDeviceDescriptor device,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds = 0d)
    {
        ThrowIfDisposed();
        return new OpenVrInputControllerPoseSourceAdapter(
            _runtime,
            StopwatchMonotonicClock.Instance,
            device,
            trackingUniverse,
            predictionOffsetSeconds);
    }

    public TrackedPoseSource CreateTrackedPoseSource(
        SteamVrDeviceDescriptor device,
        double predictionOffsetSeconds = 0d)
    {
        return CreateTrackedPoseSource(
            device,
            OpenVrTrackingUniverse.Standing,
            predictionOffsetSeconds);
    }

    /// <summary>
    /// Creates a tracked-device source in the explicitly selected OpenVR
    /// coordinate frame. The first-party driver path selects
    /// <see cref="OpenVrTrackingUniverse.RawAndUncalibrated"/> so its composed
    /// output remains in raw driver space; legacy callers retain Standing by
    /// using the overload without a universe argument.
    /// </summary>
    public TrackedPoseSource CreateTrackedPoseSource(
        SteamVrDeviceDescriptor device,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds = 0d)
    {
        ThrowIfDisposed();
        return new OpenVrTrackedPoseSourceAdapter(
            _runtime,
            StopwatchMonotonicClock.Instance,
            device,
            trackingUniverse,
            predictionOffsetSeconds);
    }

    /// <summary>
    /// Creates a reusable source that samples the requested distinct tracked
    /// devices from one logical OpenVR acquisition. All returned samples share
    /// the same post-call monotonic host-ingress timestamp.
    /// </summary>
    public TrackedPoseBatchSource CreateTrackedPoseBatchSource(
        IEnumerable<SteamVrDeviceDescriptor> devices,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds = 0d)
    {
        ThrowIfDisposed();
        return new OpenVrTrackedPoseBatchSourceAdapter(
            _runtime,
            StopwatchMonotonicClock.Instance,
            devices,
            trackingUniverse,
            predictionOffsetSeconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _runtime.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class OpenVrRuntimeInitializationException : Exception
{
    public OpenVrRuntimeInitializationException(string message)
        : base(message)
    {
    }
}
