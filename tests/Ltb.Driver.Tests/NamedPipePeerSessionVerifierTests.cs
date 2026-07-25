using Microsoft.Win32.SafeHandles;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

[Collection(DriverLifecycleTestGroup.Name)]
public sealed class NamedPipePeerSessionVerifierTests
{
    [Fact]
    public void ExactSameSessionIsAccepted()
    {
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession();
        var verifier = new NamedPipePeerSessionVerifier(nativeApi);
        using var pipeHandle = new SafePipeHandle(new IntPtr(1), ownsHandle: false);

        verifier.Verify(pipeHandle);

        Assert.Equal(1, nativeApi.ServerProcessLookupCount);
        Assert.Equal(
            new[] { nativeApi.ServerProcessId, checked((uint)Environment.ProcessId) },
            nativeApi.SessionLookupProcessIds);
    }

    [Fact]
    public void ServerProcessLookupFailureIsRejectedDeterministically()
    {
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession() with
        {
            ServerProcessLookupSucceeds = false,
            ServerProcessErrorCode = 5,
        };
        var verifier = new NamedPipePeerSessionVerifier(nativeApi);
        using var pipeHandle = new SafePipeHandle(new IntPtr(1), ownsHandle: false);

        var exception = Assert.Throws<DriverPeerAuthorizationException>(
            () => verifier.Verify(pipeHandle));

        Assert.Equal(
            "Named-pipe server authorization failed: GetNamedPipeServerProcessId returned Win32 error 5.",
            exception.Message);
        Assert.Empty(nativeApi.SessionLookupProcessIds);
    }

    [Fact]
    public void ServerSessionLookupFailureIsRejectedDeterministically()
    {
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession() with
        {
            ServerSessionLookupSucceeds = false,
            ServerSessionErrorCode = 87,
        };
        var verifier = new NamedPipePeerSessionVerifier(nativeApi);
        using var pipeHandle = new SafePipeHandle(new IntPtr(1), ownsHandle: false);

        var exception = Assert.Throws<DriverPeerAuthorizationException>(
            () => verifier.Verify(pipeHandle));

        Assert.Equal(
            $"Named-pipe server authorization failed: ProcessIdToSessionId returned Win32 error 87 for server process {nativeApi.ServerProcessId}.",
            exception.Message);
        Assert.Equal(new[] { nativeApi.ServerProcessId }, nativeApi.SessionLookupProcessIds);
    }

    [Fact]
    public void ClientSessionLookupFailureIsRejectedDeterministically()
    {
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession() with
        {
            ClientSessionLookupSucceeds = false,
            ClientSessionErrorCode = 6,
        };
        var verifier = new NamedPipePeerSessionVerifier(nativeApi);
        using var pipeHandle = new SafePipeHandle(new IntPtr(1), ownsHandle: false);

        var exception = Assert.Throws<DriverPeerAuthorizationException>(
            () => verifier.Verify(pipeHandle));

        uint clientProcessId = checked((uint)Environment.ProcessId);
        Assert.Equal(
            $"Named-pipe server authorization failed: ProcessIdToSessionId returned Win32 error 6 for client process {clientProcessId}.",
            exception.Message);
        Assert.Equal(
            new[] { nativeApi.ServerProcessId, clientProcessId },
            nativeApi.SessionLookupProcessIds);
    }

    [Fact]
    public void SessionMismatchIsRejectedDeterministically()
    {
        var nativeApi = FakeNamedPipeSessionNativeApi.SameSession() with
        {
            ServerSessionId = 7,
            ClientSessionId = 8,
        };
        var verifier = new NamedPipePeerSessionVerifier(nativeApi);
        using var pipeHandle = new SafePipeHandle(new IntPtr(1), ownsHandle: false);

        var exception = Assert.Throws<DriverPeerAuthorizationException>(
            () => verifier.Verify(pipeHandle));

        Assert.Equal(
            "Named-pipe server authorization failed: server session 7 does not match client session 8.",
            exception.Message);
    }
}

internal sealed record FakeNamedPipeSessionNativeApi : INamedPipeSessionNativeApi
{
    private readonly List<uint> _sessionLookupProcessIds = [];

    public bool IsSupported { get; init; } = true;

    public bool ServerProcessLookupSucceeds { get; init; } = true;

    public uint ServerProcessId { get; init; } = 4_000_000_000;

    public int ServerProcessErrorCode { get; init; }

    public bool ServerSessionLookupSucceeds { get; init; } = true;

    public uint ServerSessionId { get; init; } = 9;

    public int ServerSessionErrorCode { get; init; }

    public bool ClientSessionLookupSucceeds { get; init; } = true;

    public uint ClientSessionId { get; init; } = 9;

    public int ClientSessionErrorCode { get; init; }

    public int ServerProcessLookupCount { get; private set; }

    public IReadOnlyList<uint> SessionLookupProcessIds => _sessionLookupProcessIds;

    public static FakeNamedPipeSessionNativeApi SameSession() => new();

    public bool TryGetServerProcessId(
        SafePipeHandle connectedPipe,
        out uint serverProcessId,
        out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        ServerProcessLookupCount++;
        serverProcessId = ServerProcessId;
        errorCode = ServerProcessErrorCode;
        return ServerProcessLookupSucceeds;
    }

    public bool TryGetProcessSessionId(
        uint processId,
        out uint sessionId,
        out int errorCode)
    {
        _sessionLookupProcessIds.Add(processId);
        if (processId == ServerProcessId)
        {
            sessionId = ServerSessionId;
            errorCode = ServerSessionErrorCode;
            return ServerSessionLookupSucceeds;
        }

        if (processId == checked((uint)Environment.ProcessId))
        {
            sessionId = ClientSessionId;
            errorCode = ClientSessionErrorCode;
            return ClientSessionLookupSucceeds;
        }

        sessionId = 0;
        errorCode = 87;
        return false;
    }
}

internal sealed class BlockingNamedPipePeerSessionVerifier : INamedPipePeerSessionVerifier, IDisposable
{
    private readonly ManualResetEventSlim _release = new();

    public TaskCompletionSource Entered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void EnsureSupported()
    {
    }

    public void Verify(SafePipeHandle connectedPipe)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        Entered.TrySetResult();
        _release.Wait();
    }

    public void Release() => _release.Set();

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }
}
