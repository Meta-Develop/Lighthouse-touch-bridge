using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ltb.MetaLink.Interop;

internal interface IOvrNativeApiFactory
{
    bool TryCreate(out IOvrNativeApi? api, out OvrRuntimeLoadFailure? failure);
}

internal sealed record OvrRuntimeLoadFailure(MetaLinkReadiness Readiness, string Diagnostic);

internal interface IOvrNativeApi : IDisposable
{
    int Initialize(ref OvrInitParams parameters);

    void Shutdown();

    int Create(out IntPtr session, out OvrGraphicsLuid luid);

    void Destroy(IntPtr session);

    double GetTimeInSeconds();

    OvrTrackingState GetTrackingState(IntPtr session, double absoluteTimeSeconds);

    int GetInputState(IntPtr session, uint controllerType, out OvrInputState inputState);

    uint GetConnectedControllerTypes(IntPtr session);

    int GetSessionStatus(IntPtr session, out OvrSessionStatus sessionStatus);

    string GetLastErrorDiagnostic(int fallbackResult);
}

internal interface IOvrRuntimeLocator
{
    bool TryLocate(out string? fullPath, out string diagnostic);
}

internal sealed class WindowsOvrRuntimeLocator : IOvrRuntimeLocator
{
    private const string RegistryPath = @"Software\Oculus VR, LLC\Oculus";
    private const string RuntimeRelativePath = @"Support\oculus-runtime\LibOVRRT64_1.dll";

    public bool TryLocate(out string? fullPath, out string diagnostic)
    {
        fullPath = null;
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "Meta Quest Link ingestion is Windows-only; the Meta runtime was not accessed.";
            return false;
        }

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry32);
            using var oculus = localMachine.OpenSubKey(RegistryPath, writable: false);
            var basePath = oculus?.GetValue("Base") as string;
            if (string.IsNullOrWhiteSpace(basePath))
            {
                diagnostic = $"Meta Quest Link registry value HKLM\\{RegistryPath}\\Base is missing.";
                return false;
            }

            if (!Path.IsPathFullyQualified(basePath))
            {
                diagnostic = "Meta Quest Link registry Base value is not an absolute installation path.";
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(basePath, RuntimeRelativePath));
            if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
            {
                diagnostic = "Meta Quest Link is registered, but LibOVRRT64_1.dll is missing from its runtime directory.";
                return false;
            }

            fullPath = candidate;
            diagnostic = "Meta Quest Link LibOVR runtime located.";
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            diagnostic = $"Meta Quest Link runtime discovery failed: {exception.Message}";
            return false;
        }
    }
}

internal sealed class OvrNativeApiFactory : IOvrNativeApiFactory
{
    private readonly IOvrRuntimeLocator _locator;

    internal OvrNativeApiFactory(IOvrRuntimeLocator? locator = null) =>
        _locator = locator ?? new WindowsOvrRuntimeLocator();

    public bool TryCreate(out IOvrNativeApi? api, out OvrRuntimeLoadFailure? failure)
    {
        api = null;
        failure = null;
        try
        {
            OvrAbiLayout.Verify();
        }
        catch (PlatformNotSupportedException exception)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.RuntimeAbiUnsupported,
                exception.Message);
            return false;
        }

        if (!_locator.TryLocate(out var fullPath, out var diagnostic))
        {
            failure = new OvrRuntimeLoadFailure(MetaLinkReadiness.MetaRuntimeMissing, diagnostic);
            return false;
        }

        try
        {
            api = OvrDynamicApi.Load(fullPath!);
            return true;
        }
        catch (EntryPointNotFoundException exception)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.RuntimeAbiUnsupported,
                $"Installed LibOVR does not expose the SDK 32.0.0 ABI: {exception.Message}");
            return false;
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or DllNotFoundException or FileLoadException)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.RuntimeIncompatible,
                $"Installed LibOVR could not be loaded: {exception.Message}");
            return false;
        }
    }
}

/// <summary>
/// Isolates the ABI-sensitive, large-struct return of ovr_GetTrackingState.
/// Linux layout tests validate the managed shape, but a Windows x64 ABI-oracle
/// test against the installed SDK/runtime remains required before release.
/// </summary>
internal interface IOvrTrackingStateReturnBoundary
{
    OvrTrackingState Invoke(IntPtr session, double absoluteTimeSeconds, OvrBool latencyMarker);
}

internal sealed class OvrTrackingStateReturnBoundary : IOvrTrackingStateReturnBoundary
{
    private readonly GetTrackingStateDelegate _delegate;

    internal OvrTrackingStateReturnBoundary(IntPtr export) =>
        _delegate = Marshal.GetDelegateForFunctionPointer<GetTrackingStateDelegate>(export);

    public OvrTrackingState Invoke(
        IntPtr session,
        double absoluteTimeSeconds,
        OvrBool latencyMarker) =>
        _delegate(session, absoluteTimeSeconds, latencyMarker);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate OvrTrackingState GetTrackingStateDelegate(
        IntPtr session,
        double absoluteTimeSeconds,
        OvrBool latencyMarker);
}

internal sealed class OvrDynamicApi : IOvrNativeApi
{
    private readonly IntPtr _library;
    private readonly InitializeDelegate _initialize;
    private readonly ShutdownDelegate _shutdown;
    private readonly CreateDelegate _create;
    private readonly DestroyDelegate _destroy;
    private readonly GetTimeInSecondsDelegate _getTimeInSeconds;
    private readonly IOvrTrackingStateReturnBoundary _trackingState;
    private readonly GetInputStateDelegate _getInputState;
    private readonly GetConnectedControllerTypesDelegate _getConnectedControllerTypes;
    private readonly GetSessionStatusDelegate _getSessionStatus;
    private readonly GetLastErrorInfoDelegate _getLastErrorInfo;
    private bool _disposed;

    private OvrDynamicApi(IntPtr library)
    {
        _library = library;
        _initialize = Resolve<InitializeDelegate>("ovr_Initialize");
        _shutdown = Resolve<ShutdownDelegate>("ovr_Shutdown");
        _create = Resolve<CreateDelegate>("ovr_Create");
        _destroy = Resolve<DestroyDelegate>("ovr_Destroy");
        _getTimeInSeconds = Resolve<GetTimeInSecondsDelegate>("ovr_GetTimeInSeconds");
        _trackingState = new OvrTrackingStateReturnBoundary(Export("ovr_GetTrackingState"));
        _getInputState = Resolve<GetInputStateDelegate>("ovr_GetInputState");
        _getConnectedControllerTypes = Resolve<GetConnectedControllerTypesDelegate>(
            "ovr_GetConnectedControllerTypes");
        _getSessionStatus = Resolve<GetSessionStatusDelegate>("ovr_GetSessionStatus");
        _getLastErrorInfo = Resolve<GetLastErrorInfoDelegate>("ovr_GetLastErrorInfo");
    }

    internal static OvrDynamicApi Load(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("LibOVR must be loaded by full path.", nameof(fullPath));
        }

        var library = NativeLibrary.Load(fullPath);
        try
        {
            return new OvrDynamicApi(library);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public int Initialize(ref OvrInitParams parameters)
    {
        ThrowIfDisposed();
        return _initialize(ref parameters);
    }

    public void Shutdown()
    {
        ThrowIfDisposed();
        _shutdown();
    }

    public int Create(out IntPtr session, out OvrGraphicsLuid luid)
    {
        ThrowIfDisposed();
        return _create(out session, out luid);
    }

    public void Destroy(IntPtr session)
    {
        ThrowIfDisposed();
        _destroy(session);
    }

    public double GetTimeInSeconds()
    {
        ThrowIfDisposed();
        return _getTimeInSeconds();
    }

    public OvrTrackingState GetTrackingState(IntPtr session, double absoluteTimeSeconds)
    {
        ThrowIfDisposed();
        return _trackingState.Invoke(session, absoluteTimeSeconds, OvrBool.False);
    }

    public int GetInputState(
        IntPtr session,
        uint controllerType,
        out OvrInputState inputState)
    {
        ThrowIfDisposed();
        return _getInputState(session, controllerType, out inputState);
    }

    public uint GetConnectedControllerTypes(IntPtr session)
    {
        ThrowIfDisposed();
        return _getConnectedControllerTypes(session);
    }

    public int GetSessionStatus(IntPtr session, out OvrSessionStatus sessionStatus)
    {
        ThrowIfDisposed();
        return _getSessionStatus(session, out sessionStatus);
    }

    public string GetLastErrorDiagnostic(int fallbackResult)
    {
        ThrowIfDisposed();
        _getLastErrorInfo(out var error);
        var message = error.GetMessage();
        var result = error.Result != 0 ? error.Result : fallbackResult;
        return string.IsNullOrWhiteSpace(message)
            ? $"LibOVR call failed with result {result}."
            : $"LibOVR call failed with result {result}: {message}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeLibrary.Free(_library);
    }

    private IntPtr Export(string name) => NativeLibrary.GetExport(_library, name);

    private T Resolve<T>(string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Export(name));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate(ref OvrInitParams parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(out IntPtr session, out OvrGraphicsLuid luid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate double GetTimeInSecondsDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetInputStateDelegate(
        IntPtr session,
        uint controllerType,
        out OvrInputState inputState);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetConnectedControllerTypesDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSessionStatusDelegate(
        IntPtr session,
        out OvrSessionStatus sessionStatus);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetLastErrorInfoDelegate(out OvrErrorInfo errorInfo);
}
