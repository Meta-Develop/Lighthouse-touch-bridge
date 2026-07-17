using System.Text.Json;
using Ltb.OpenVr;
using Ltb.Vmt;

namespace Ltb.App;

internal sealed class OpenVrOneHandBridgeRuntime : IOneHandBridgeRuntime
{
    private readonly OpenVrSession _session;

    public OpenVrOneHandBridgeRuntime(OpenVrSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices() =>
        _session.EnumerateDevices();

    public TrackedPoseSource CreateTrackedPoseSource(SteamVrDeviceDescriptor device) =>
        _session.CreateTrackedPoseSource(device);
}

internal sealed class VmtClientOneHandBridgeController :
    IOneHandBridgeVmtController,
    IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly VmtClient _client;
    private CancellationTokenSource? _pumpStop;
    private Task? _pumpTask;

    public VmtClientOneHandBridgeController(VmtClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsAlive
    {
        get
        {
            ThrowIfPumpFaulted();
            return _client.DriverHealth.IsAlive;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pumpTask is not null)
        {
            return ValueTask.CompletedTask;
        }

        _pumpStop = new CancellationTokenSource();
        _pumpTask = PumpResponsesAsync(_pumpStop.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask WaitForAliveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutStop = new CancellationTokenSource(timeout);
        using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutStop.Token);
        try
        {
            while (!IsAlive)
            {
                await Task.Delay(PollInterval, waitStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            timeoutStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No fresh VMT /VMT/Out/Alive heartbeat arrived within {timeout.TotalSeconds:R} seconds. " +
                "Confirm VMT is running and that response port 39571 is available.");
        }
    }

    public ValueTask ActivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken) =>
        _client.ActivateAsync(configuration, cancellationToken);

    public ValueTask DeactivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken) =>
        _client.DeactivateAsync(configuration, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_pumpStop is not null)
        {
            await _pumpStop.CancelAsync().ConfigureAwait(false);
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_pumpStop?.IsCancellationRequested == true)
            {
            }
            catch
            {
                // The coordinator observes pump faults through IsAlive while
                // active. Disposal must not obscure its result or cleanup.
            }
        }

        _pumpStop?.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _client.ObserveNextResponseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ThrowIfPumpFaulted()
    {
        if (_pumpTask is null)
        {
            throw new InvalidOperationException("The VMT heartbeat pump has not been started.");
        }

        if (_pumpTask.IsFaulted)
        {
            throw new InvalidOperationException(
                "The VMT heartbeat response pump stopped unexpectedly.",
                _pumpTask.Exception?.GetBaseException());
        }

        if (_pumpTask.IsCanceled && _pumpStop?.IsCancellationRequested != true)
        {
            throw new InvalidOperationException(
                "The VMT heartbeat response pump was cancelled unexpectedly.");
        }
    }
}

internal sealed class SteamVrOneHandBridgeOverrideController :
    IOneHandBridgeOverrideController
{
    private readonly SteamVrSettingsManager _settings;

    public SteamVrOneHandBridgeOverrideController(SteamVrSettingsManager settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Prepare(TrackingOverrideBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _ = _settings.ReleaseOverride(binding);
        RejectHandOwnershipConflict(binding);
    }

    public void Enable(TrackingOverrideBinding binding) =>
        _ = _settings.EnableOverride(binding);

    public void Release(TrackingOverrideBinding binding) =>
        _ = _settings.ReleaseOverride(binding);

    private void RejectHandOwnershipConflict(TrackingOverrideBinding binding)
    {
        using var settings = JsonDocument.Parse(File.ReadAllBytes(_settings.SettingsFilePath));
        if (!settings.RootElement.TryGetProperty("TrackingOverrides", out var overrides))
        {
            return;
        }

        if (overrides.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "SteamVR settings property 'TrackingOverrides' must be a JSON object.");
        }

        foreach (var entry in overrides.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"TrackingOverrides entry '{entry.Name}' must have a string target path.");
            }

            if (string.Equals(
                    entry.Value.GetString(),
                    binding.SemanticHandPath,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Semantic hand '{binding.SemanticHandPath}' is already supplied by " +
                    $"'{entry.Name}'. Release that owner before starting LTB.");
            }
        }
    }
}

internal sealed class DeferredWindowsVerificationProbe :
    IOneHandBridgeVerificationProbe
{
    public ValueTask<OneHandBridgeVerificationObservation> ObserveAsync(
        OneHandBridgeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OneHandBridgeVerificationObservation.Deferred(
            "The live OpenVR adapter can verify device/pose health, but Touch input provenance " +
            "and composed-pose provenance require the documented Windows SteamVR hardware check."));
    }
}

internal sealed class SystemOneHandBridgeDelay : IOneHandBridgeDelay
{
    public async ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}
