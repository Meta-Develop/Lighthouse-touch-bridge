namespace Ltb.Vmt;

/// <summary>
/// Controls one or more bounded VMT slots through a fakeable OSC transport and
/// exposes driver heartbeat freshness to the runtime safety coordinator.
/// </summary>
public sealed class VmtClient : IAsyncDisposable
{
    private readonly IVmtDatagramTransport _transport;
    private readonly VmtDriverHealthMonitor _healthMonitor;

    public VmtClient(
        IVmtDatagramTransport transport,
        TimeSpan heartbeatStaleAfter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _healthMonitor = new VmtDriverHealthMonitor(heartbeatStaleAfter, timeProvider);
    }

    public VmtDriverHealthSnapshot DriverHealth => _healthMonitor.Snapshot;

    /// <summary>
    /// Configures the serial-following local mount transform and activates the
    /// virtual device. Auto pose update is enabled first so VMT recomputes the
    /// serial-following Joint pose every frame.
    /// </summary>
    public async ValueTask ActivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await _transport.SendAsync(
            VmtOscProtocol.EncodeSetAutoPoseUpdate(enabled: true),
            cancellationToken).ConfigureAwait(false);
        await SendConfigurationAsync(
            configuration,
            enabled: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disables the virtual device. VMT documents that disabling its source
    /// device releases the corresponding SteamVR TrackingOverride. This does
    /// not disable VMT's global auto-pose setting because other VMT devices or
    /// applications may still depend on it.
    /// </summary>
    public ValueTask DeactivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SendConfigurationAsync(configuration, enabled: false, cancellationToken);

    public bool ObserveDriverDatagram(ReadOnlySpan<byte> datagram) =>
        _healthMonitor.ObserveDriverDatagram(datagram);

    /// <summary>Receives and observes one VMT response datagram.</summary>
    /// <returns><see langword="true"/> when the datagram was an Alive heartbeat.</returns>
    public async ValueTask<bool> ObserveNextResponseAsync(
        CancellationToken cancellationToken = default)
    {
        var received = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        if (_transport is IVmtResponseSourceFilter sourceFilter &&
            !sourceFilter.IsExpectedResponseSource(received.RemoteEndpoint))
        {
            return false;
        }

        return _healthMonitor.ObserveDriverDatagram(received.Payload.Span);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private ValueTask SendConfigurationAsync(
        VmtDeviceConfiguration configuration,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var packet = VmtOscProtocol.EncodeJointDriver(configuration, enabled);
        return _transport.SendAsync(packet, cancellationToken);
    }
}
