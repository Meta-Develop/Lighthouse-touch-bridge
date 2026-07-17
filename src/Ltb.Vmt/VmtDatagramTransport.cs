using System.Net;
using System.Net.Sockets;

namespace Ltb.Vmt;

/// <summary>One OSC datagram received from the VMT response socket.</summary>
public readonly record struct VmtReceivedDatagram(
    ReadOnlyMemory<byte> Payload,
    IPEndPoint RemoteEndpoint);

/// <summary>
/// Fakeable duplex transport for VMT OSC. Production sends and response
/// listening are kept behind this boundary so tests never require VMT or a
/// real network endpoint.
/// </summary>
public interface IVmtDatagramTransport : IAsyncDisposable
{
    ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default);

    ValueTask<VmtReceivedDatagram> ReceiveAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional receive-source policy used by production transports. The VMT
/// response source port is not part of this contract because it can vary.
/// </summary>
public interface IVmtResponseSourceFilter
{
    bool IsExpectedResponseSource(IPEndPoint remoteEndpoint);
}

public static class VmtResponseSource
{
    public static bool HasExpectedDriverAddress(
        IPAddress expectedDriverAddress,
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(expectedDriverAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        return Normalize(expectedDriverAddress).Equals(Normalize(remoteAddress));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>UDP transport for VMT's command and response ports.</summary>
public sealed class UdpVmtDatagramTransport : IVmtDatagramTransport, IVmtResponseSourceFilter
{
    public const int DefaultDriverPort = 39570;
    public const int DefaultResponsePort = 39571;

    private readonly UdpClient _sender;
    private readonly UdpClient _receiver;
    private readonly IPEndPoint _driverEndpoint;
    private bool _disposed;

    /// <summary>
    /// Creates the transport without assuming a response binding. VMT Manager
    /// also uses port 39571, so callers must deliberately choose an available
    /// endpoint and coordinate that port ownership.
    /// </summary>
    public UdpVmtDatagramTransport(
        IPEndPoint driverEndpoint,
        IPEndPoint responseListenEndpoint)
    {
        ArgumentNullException.ThrowIfNull(driverEndpoint);
        ArgumentNullException.ThrowIfNull(responseListenEndpoint);

        if (driverEndpoint.AddressFamily != responseListenEndpoint.AddressFamily)
        {
            throw new ArgumentException(
                "The VMT driver and response endpoints must use the same address family.",
                nameof(responseListenEndpoint));
        }

        _driverEndpoint = driverEndpoint;
        _sender = new UdpClient(driverEndpoint.AddressFamily);
        _receiver = new UdpClient(responseListenEndpoint.AddressFamily);

        try
        {
            _receiver.Client.Bind(responseListenEndpoint);
        }
        catch
        {
            _sender.Dispose();
            _receiver.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sender.SendAsync(datagram, _driverEndpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<VmtReceivedDatagram> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var received = await _receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return new VmtReceivedDatagram(received.Buffer, received.RemoteEndPoint);
    }

    public bool IsExpectedResponseSource(IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        return VmtResponseSource.HasExpectedDriverAddress(
            _driverEndpoint.Address,
            remoteEndpoint.Address);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _sender.Dispose();
            _receiver.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
