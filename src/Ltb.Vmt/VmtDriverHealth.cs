namespace Ltb.Vmt;

public enum VmtDriverHealthState
{
    NeverObserved,
    Alive,
    Stale,
}

/// <summary>Point-in-time VMT driver heartbeat state.</summary>
public readonly record struct VmtDriverHealthSnapshot(
    VmtDriverHealthState State,
    string? Version,
    string? InstallPath,
    DateTimeOffset? LastHeartbeatUtc,
    TimeSpan? HeartbeatAge,
    TimeSpan StaleAfter)
{
    public bool HasHeartbeat => LastHeartbeatUtc.HasValue;

    public bool IsAlive => State == VmtDriverHealthState.Alive;

    public bool IsStale => State == VmtDriverHealthState.Stale;
}

/// <summary>Observes VMT <c>/VMT/Out/Alive</c> responses and detects staleness.</summary>
public sealed class VmtDriverHealthMonitor
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _lastHeartbeatUtc;
    private long? _lastHeartbeatTimestamp;
    private string? _version;
    private string? _installPath;

    public VmtDriverHealthMonitor(
        TimeSpan staleAfter,
        TimeProvider? timeProvider = null)
    {
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter),
                staleAfter,
                "VMT heartbeat staleness threshold must be positive.");
        }

        StaleAfter = staleAfter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan StaleAfter { get; }

    public VmtDriverHealthSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                if (!_lastHeartbeatTimestamp.HasValue)
                {
                    return new VmtDriverHealthSnapshot(
                        VmtDriverHealthState.NeverObserved,
                        null,
                        null,
                        null,
                        null,
                        StaleAfter);
                }

                var age = _timeProvider.GetElapsedTime(
                    _lastHeartbeatTimestamp.Value,
                    _timeProvider.GetTimestamp());
                var monotonicClockRegressed = age < TimeSpan.Zero;
                if (age < TimeSpan.Zero)
                {
                    age = TimeSpan.Zero;
                }

                return new VmtDriverHealthSnapshot(
                    !monotonicClockRegressed && age < StaleAfter
                        ? VmtDriverHealthState.Alive
                        : VmtDriverHealthState.Stale,
                    _version,
                    _installPath,
                    _lastHeartbeatUtc,
                    age,
                    StaleAfter);
            }
        }
    }

    public bool ObserveDriverDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!VmtOscProtocol.TryDecodeAlive(datagram, out var alive))
        {
            return false;
        }

        var receivedTimestamp = _timeProvider.GetTimestamp();
        var receivedAtUtc = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (!_lastHeartbeatTimestamp.HasValue ||
                _timeProvider.GetElapsedTime(
                    _lastHeartbeatTimestamp.Value,
                    receivedTimestamp) >= TimeSpan.Zero)
            {
                _lastHeartbeatTimestamp = receivedTimestamp;
                _lastHeartbeatUtc = receivedAtUtc;
                _version = alive.Version;
                _installPath = alive.InstallPath;
            }
        }

        return true;
    }
}
