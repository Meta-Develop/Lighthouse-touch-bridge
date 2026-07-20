using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    bool TryLocate(out string? fullPath, out OvrRuntimeLoadFailure? failure);
}

internal interface IOvrRuntimePlatform
{
    bool IsWindowsX64 { get; }
}

internal interface IOvrRuntimeRegistry
{
    string? ReadString(
        OvrRegistryHive hive,
        OvrRegistryView view,
        string subKeyPath,
        string valueName);
}

internal enum OvrRegistryHive
{
    LocalMachine,
}

internal enum OvrRegistryView
{
    Registry32,
}

internal interface IOvrRuntimeFileSystem
{
    bool IsPathFullyQualified(string path);

    string Combine(string basePath, params string[] relativeSegments);

    string GetFullPath(string path);

    bool FileExists(string path);
}

internal sealed class SystemOvrRuntimePlatform : IOvrRuntimePlatform
{
    public bool IsWindowsX64 =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;
}

internal sealed class WindowsOvrRuntimeRegistry : IOvrRuntimeRegistry
{
    [SupportedOSPlatform("windows")]
    public string? ReadString(
        OvrRegistryHive hive,
        OvrRegistryView view,
        string subKeyPath,
        string valueName)
    {
        var windowsHive = hive switch
        {
            OvrRegistryHive.LocalMachine => RegistryHive.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(hive)),
        };
        var windowsView = view switch
        {
            OvrRegistryView.Registry32 => RegistryView.Registry32,
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        };
        using var baseKey = RegistryKey.OpenBaseKey(windowsHive, windowsView);
        using var subKey = baseKey.OpenSubKey(subKeyPath, writable: false);
        return subKey?.GetValue(valueName) as string;
    }
}

internal sealed class SystemOvrRuntimeFileSystem : IOvrRuntimeFileSystem
{
    public bool IsPathFullyQualified(string path) => Path.IsPathFullyQualified(path);

    public string Combine(string basePath, params string[] relativeSegments)
    {
        var parts = new string[relativeSegments.Length + 1];
        parts[0] = basePath;
        relativeSegments.CopyTo(parts, 1);
        return Path.Combine(parts);
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool FileExists(string path) => File.Exists(path);
}

internal sealed class WindowsOvrRuntimeLocator : IOvrRuntimeLocator
{
    internal const string RegistryPath = @"Software\Oculus VR, LLC\Oculus";
    internal const string RegistryValueName = "Base";
    internal const string RuntimeDllName = "LibOVRRT64_1.dll";
    private readonly IOvrRuntimePlatform _platform;
    private readonly IOvrRuntimeRegistry _registry;
    private readonly IOvrRuntimeFileSystem _fileSystem;

    internal WindowsOvrRuntimeLocator(
        IOvrRuntimePlatform? platform = null,
        IOvrRuntimeRegistry? registry = null,
        IOvrRuntimeFileSystem? fileSystem = null)
    {
        _platform = platform ?? new SystemOvrRuntimePlatform();
        _registry = registry ?? new WindowsOvrRuntimeRegistry();
        _fileSystem = fileSystem ?? new SystemOvrRuntimeFileSystem();
    }

    public bool TryLocate(out string? fullPath, out OvrRuntimeLoadFailure? failure)
    {
        fullPath = null;
        failure = null;
        if (!_platform.IsWindowsX64)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.AbiUnavailable,
                "Meta Link requires Windows x64. Run LTB on a supported Windows x64 host.");
            return false;
        }

        try
        {
            var basePath = _registry.ReadString(
                OvrRegistryHive.LocalMachine,
                OvrRegistryView.Registry32,
                RegistryPath,
                RegistryValueName);
            if (string.IsNullOrWhiteSpace(basePath))
            {
                failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.NotInstalled,
                    $"Meta runtime registration HKLM Registry32\\{RegistryPath}\\{RegistryValueName} is missing. Install or repair Meta Horizon Link.");
                return false;
            }

            if (!_fileSystem.IsPathFullyQualified(basePath))
            {
                failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.NotInstalled,
                    "Meta runtime Registry32 Base value is not an absolute installation path. Repair Meta Horizon Link registration.");
                return false;
            }

            var candidate = _fileSystem.GetFullPath(_fileSystem.Combine(
                basePath,
                "Support",
                "oculus-runtime",
                RuntimeDllName));
            if (!_fileSystem.IsPathFullyQualified(candidate) || !_fileSystem.FileExists(candidate))
            {
                failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.NotInstalled,
                    "Meta runtime is registered, but Support\\oculus-runtime\\LibOVRRT64_1.dll is missing. Repair Meta Horizon Link.");
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.Faulted,
                $"Meta runtime discovery failed unexpectedly: {exception.Message} Check registry access and repair Meta Horizon Link.");
            return false;
        }
    }
}

internal interface IOvrNativeApiLoader
{
    IOvrNativeApi Load(string fullPath);
}

internal sealed class OvrNativeApiLoader : IOvrNativeApiLoader
{
    public IOvrNativeApi Load(string fullPath) => OvrDynamicApi.Load(fullPath);
}

internal sealed class OvrNativeApiFactory : IOvrNativeApiFactory
{
    private readonly IOvrRuntimeLocator _locator;
    private readonly IOvrNativeApiLoader _loader;

    internal OvrNativeApiFactory(
        IOvrRuntimeLocator? locator = null,
        IOvrNativeApiLoader? loader = null)
    {
        _locator = locator ?? new WindowsOvrRuntimeLocator();
        _loader = loader ?? new OvrNativeApiLoader();
    }

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
                MetaLinkReadiness.AbiUnavailable,
                $"LibOVR ABI layout is unavailable: {exception.Message} Use the supported Windows x64 runtime.");
            return false;
        }

        if (!_locator.TryLocate(out var fullPath, out var locationFailure))
        {
            failure = locationFailure ?? new OvrRuntimeLoadFailure(
                MetaLinkReadiness.Faulted,
                "Meta runtime discovery failed without a diagnostic. Restart LTB and inspect runtime diagnostics.");
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath))
            {
                failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.Faulted,
                    "Meta runtime discovery returned a non-absolute DLL path. Repair Meta Horizon Link registration.");
                return false;
            }

            api = _loader.Load(fullPath);
            return true;
        }
        catch (EntryPointNotFoundException exception)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.AbiUnavailable,
                $"Installed LibOVR does not expose the SDK 32.0.0 ABI: {exception.Message} Repair or update Meta Horizon Link.");
            return false;
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or DllNotFoundException or FileLoadException)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.AbiUnavailable,
                $"Installed LibOVR could not be loaded by its registered full path: {exception.Message} Repair Meta Horizon Link.");
            return false;
        }
        catch (Exception exception)
        {
            failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.Faulted,
                $"Installed LibOVR load failed unexpectedly: {exception.Message} Restart LTB and inspect runtime diagnostics.");
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
