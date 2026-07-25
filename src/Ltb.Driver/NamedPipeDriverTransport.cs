using System.IO.Pipes;

namespace Ltb.Driver;

public sealed class DriverTransportDisconnectedException : IOException
{
    public DriverTransportDisconnectedException(string message)
        : base(message)
    {
    }

    public DriverTransportDisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class NamedPipeDriverTransportFactory : IDriverTransportFactory
{
    public const string DefaultPipeName = "lighthouse-touch-bridge-v1";
    private readonly string _pipeName;

    public NamedPipeDriverTransportFactory(string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("The pipe name must be a local name, not a path.", nameof(pipeName));
        }

        _pipeName = pipeName;
    }

    public IDriverTransport Create() => new NamedPipeDriverTransport(_pipeName);
}

public sealed class NamedPipeDriverTransport : IDriverTransport
{
    private readonly string _pipeName;
    private readonly INamedPipePeerSessionVerifier _peerSessionVerifier;
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public NamedPipeDriverTransport(string pipeName)
        : this(pipeName, new NamedPipePeerSessionVerifier())
    {
    }

    internal NamedPipeDriverTransport(
        string pipeName,
        INamedPipePeerSessionVerifier peerSessionVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _peerSessionVerifier = peerSessionVerifier ??
            throw new ArgumentNullException(nameof(peerSessionVerifier));
    }

    internal bool IsConnected => _pipe?.IsConnected == true;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe is not null)
        {
            throw new InvalidOperationException("The transport has already been connected.");
        }

        _peerSessionVerifier.EnsureSupported();
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _peerSessionVerifier.Verify(pipe.SafePipeHandle);
            _pipe = pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            throw new DriverTransportDisconnectedException("The named pipe is not connected.");
        }

        try
        {
            await pipe.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new DriverTransportDisconnectedException(
                "The named pipe disconnected during a driver-feed write.",
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        GC.SuppressFinalize(this);
    }
}
