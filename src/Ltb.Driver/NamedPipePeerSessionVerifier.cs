using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ltb.Driver;

internal sealed class DriverPeerAuthorizationException : UnauthorizedAccessException
{
    public DriverPeerAuthorizationException(string message)
        : base(message)
    {
    }
}

internal interface INamedPipePeerSessionVerifier
{
    void EnsureSupported();

    void Verify(SafePipeHandle connectedPipe);
}

internal interface INamedPipeSessionNativeApi
{
    bool IsSupported { get; }

    bool TryGetServerProcessId(
        SafePipeHandle connectedPipe,
        out uint serverProcessId,
        out int errorCode);

    bool TryGetProcessSessionId(
        uint processId,
        out uint sessionId,
        out int errorCode);
}

internal sealed class NamedPipePeerSessionVerifier : INamedPipePeerSessionVerifier
{
    private readonly INamedPipeSessionNativeApi _nativeApi;

    public NamedPipePeerSessionVerifier()
        : this(new WindowsNamedPipeSessionNativeApi())
    {
    }

    internal NamedPipePeerSessionVerifier(INamedPipeSessionNativeApi nativeApi)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public void EnsureSupported()
    {
        if (!_nativeApi.IsSupported)
        {
            throw new DriverPeerAuthorizationException(
                "Named-pipe server authorization requires Windows session APIs.");
        }
    }

    public void Verify(SafePipeHandle connectedPipe)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        EnsureSupported();

        if (!_nativeApi.TryGetServerProcessId(
                connectedPipe,
                out uint serverProcessId,
                out int serverProcessError))
        {
            throw new DriverPeerAuthorizationException(
                $"Named-pipe server authorization failed: GetNamedPipeServerProcessId returned Win32 error {serverProcessError}.");
        }

        if (!_nativeApi.TryGetProcessSessionId(
                serverProcessId,
                out uint serverSessionId,
                out int serverSessionError))
        {
            throw new DriverPeerAuthorizationException(
                $"Named-pipe server authorization failed: ProcessIdToSessionId returned Win32 error {serverSessionError} for server process {serverProcessId}.");
        }

        uint clientProcessId = checked((uint)Environment.ProcessId);
        if (!_nativeApi.TryGetProcessSessionId(
                clientProcessId,
                out uint clientSessionId,
                out int clientSessionError))
        {
            throw new DriverPeerAuthorizationException(
                $"Named-pipe server authorization failed: ProcessIdToSessionId returned Win32 error {clientSessionError} for client process {clientProcessId}.");
        }

        if (serverSessionId != clientSessionId)
        {
            throw new DriverPeerAuthorizationException(
                $"Named-pipe server authorization failed: server session {serverSessionId} does not match client session {clientSessionId}.");
        }
    }
}

internal sealed class WindowsNamedPipeSessionNativeApi : INamedPipeSessionNativeApi
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryGetServerProcessId(
        SafePipeHandle connectedPipe,
        out uint serverProcessId,
        out int errorCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            serverProcessId = 0;
            errorCode = 50;
            return false;
        }

        bool succeeded = GetNamedPipeServerProcessId(connectedPipe, out serverProcessId);
        errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded;
    }

    public bool TryGetProcessSessionId(
        uint processId,
        out uint sessionId,
        out int errorCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            sessionId = 0;
            errorCode = 50;
            return false;
        }

        bool succeeded = ProcessIdToSessionId(processId, out sessionId);
        errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", EntryPoint = "ProcessIdToSessionId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
