using System.Diagnostics;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>
/// Typed stopped/pre-session state for owner-selected tracker binding and
/// receipt-scoped driver lifecycle policy.
/// </summary>
public enum InternalDriverPreSessionState
{
    NotLoaded = 0,
    Ready,
    SettingsUnavailable,
    TrackerDiscoveryFailed,
    TrackerBindingIncomplete,
    SteamVrMustBeStopped,
    RegisteredDevicePathUnresolved,
    RegistrationStateRequiresAction,
    ManualBindingDecisionStale,
    RestartRequired,
    CleanupCompleted,
    CleanupSkipped,
    Faulted,
}

/// <summary>A paired physical Lighthouse tracker that may be selected by its stable serial.</summary>
public sealed record InternalDriverPairedTrackerOption(string Serial, string Model)
{
    public string Serial { get; } = RequireSerial(Serial);

    public string Model { get; } = RequireModel(Model);

    public string DisplayName => $"{Serial} · {Model}";

    private static string RequireSerial(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant();
    }

    private static string RequireModel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

/// <summary>Whether either SteamVR host process prevents an offline preflight write.</summary>
public sealed record InternalDriverSteamVrProcessSnapshot(
    bool VrServerRunning,
    bool VrMonitorRunning)
{
    public bool IsAnyRunning => VrServerRunning || VrMonitorRunning;

    public string RunningProcessList
    {
        get
        {
            var running = new List<string>(2);
            if (VrServerRunning)
            {
                running.Add("vrserver");
            }

            if (VrMonitorRunning)
            {
                running.Add("vrmonitor");
            }

            return running.Count == 0 ? "none" : string.Join(", ", running);
        }
    }
}

/// <summary>
/// Immutable state rendered by the stopped/pre-session GUI. Tracker serials
/// are canonical uppercase values; a pair is either complete or absent.
/// </summary>
public sealed record InternalDriverPreSessionSnapshot(
    InternalDriverPreSessionState State,
    IReadOnlyList<InternalDriverPairedTrackerOption> PairedTrackers,
    string? LeftTrackerSerial,
    string? RightTrackerSerial,
    bool UnregisterOnExit,
    SteamVrDriverStartupState? RegistrationState,
    InternalDriverSteamVrProcessSnapshot SteamVrProcesses,
    string Diagnostic,
    string Remediation)
{
    public bool HasManualBinding =>
        LeftTrackerSerial is not null && RightTrackerSerial is not null;

    public bool CanStart => State == InternalDriverPreSessionState.Ready;

    public bool RestartRequired => State == InternalDriverPreSessionState.RestartRequired;

    /// <summary>
    /// Read-only observations of recognized unrelated SteamVR registrations.
    /// Registration never proves that an integration is loaded, running, or
    /// publishing, and this evidence grants no mutation authority.
    /// </summary>
    public IReadOnlyList<ExternalSteamVrIntegrationWarning> ExternalRegistrationWarnings
    {
        get;
        init;
    } = [];

    /// <summary>Metadata-only sibling backup discovery; backup bytes are never read.</summary>
    public SteamVrSettingsRecoveryDiscovery? RecoveryDiscovery { get; init; }

    /// <summary>Read-only role drift for the exact paths in a retained LTB receipt.</summary>
    public TrackerRoleDrift? TrackerRoleDrift { get; init; }

    /// <summary>Current exact live-observed tracker paths and bounded prior history.</summary>
    public IReadOnlyList<TrackerPathObservation> TrackerPathObservations { get; init; } = [];

    public bool TrackerPathReconciliationPending { get; init; }

    public string TrackerPathEvidenceDiagnostic { get; init; } =
        "No live tracker-path evidence has been loaded.";

    /// <summary>
    /// Conservative read-only assessment of the exact reusable manual pair, when
    /// one complete pair can be selected without motion association.
    /// </summary>
    public StoredCalibrationProfilePairAssessment? StoredProfileQuality { get; init; }

    public static InternalDriverPreSessionSnapshot Initial { get; } = new(
        InternalDriverPreSessionState.NotLoaded,
        Array.Empty<InternalDriverPairedTrackerOption>(),
        LeftTrackerSerial: null,
        RightTrackerSerial: null,
        UnregisterOnExit: true,
        RegistrationState: null,
        new InternalDriverSteamVrProcessSnapshot(false, false),
        "Paired Lighthouse trackers and registration state have not been inspected.",
        "Refresh while the session is stopped.");
}

/// <summary>
/// App-owned stopped/pre-session composition. It is the only GUI-facing
/// boundary that reads or writes manual binding and unregister-on-exit policy.
/// </summary>
public interface IInternalDriverPreSessionControl : IAsyncDisposable
{
    InternalDriverPreSessionSnapshot CurrentSnapshot { get; }

    ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
        string leftTrackerSerial,
        string rightTrackerSerial,
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
        InternalDriverManualBindingVerificationEvidence verification,
        InternalDriverManualBindingDecision decision,
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
        CancellationToken cancellationToken = default);
}

internal interface IInternalDriverPairedTrackerDiscovery
{
    ValueTask<PairedLighthouseDeviceDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken);
}

internal sealed class InternalDriverPairedTrackerDiscoveryAdapter(
    PairedLighthouseDeviceDiscovery discovery) : IInternalDriverPairedTrackerDiscovery
{
    private readonly PairedLighthouseDeviceDiscovery _discovery = discovery ??
        throw new ArgumentNullException(nameof(discovery));

    public ValueTask<PairedLighthouseDeviceDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken) =>
        _discovery.DiscoverAsync(cancellationToken);
}

internal interface IInternalDriverSteamVrProcessInspector
{
    InternalDriverSteamVrProcessSnapshot Inspect();
}

internal sealed class SystemInternalDriverSteamVrProcessInspector :
    IInternalDriverSteamVrProcessInspector
{
    public InternalDriverSteamVrProcessSnapshot Inspect() => new(
        IsRunning("vrserver"),
        IsRunning("vrmonitor"));

    private static bool IsRunning(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        try
        {
            return processes.Length != 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// Production pre-session coordinator. Offline Lighthouse config proves serial
/// and model only, so a manual binding cannot authorize a SteamVR settings
/// write until an authoritative live registered-device path relationship has
/// been captured and designed into this boundary.
/// </summary>
public sealed class InternalDriverPreSessionControl : IInternalDriverPreSessionControl
{
    private const string ControllerModel = "Quest 2 Touch";
    private readonly InternalDriverResolvedPaths _paths;
    private readonly IInternalDriverPairedTrackerDiscovery _trackerDiscovery;
    private readonly IInternalDriverSteamVrProcessInspector _processInspector;
    private readonly Func<IInternalDriverRegistrationMaintenance> _maintenanceFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private InternalDriverPreSessionSnapshot _snapshot =
        InternalDriverPreSessionSnapshot.Initial;
    private bool _disposed;

    internal InternalDriverPreSessionControl(
        InternalDriverResolvedPaths paths,
        IInternalDriverPairedTrackerDiscovery trackerDiscovery,
        IInternalDriverSteamVrProcessInspector processInspector,
        Func<IInternalDriverRegistrationMaintenance> maintenanceFactory)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _trackerDiscovery = trackerDiscovery ??
            throw new ArgumentNullException(nameof(trackerDiscovery));
        _processInspector = processInspector ??
            throw new ArgumentNullException(nameof(processInspector));
        _maintenanceFactory = maintenanceFactory ??
            throw new ArgumentNullException(nameof(maintenanceFactory));
    }

    public InternalDriverPreSessionSnapshot CurrentSnapshot => _snapshot;

    public static InternalDriverPreSessionControl Create(
        InternalDriverSessionOptions? options = null)
    {
        options ??= new InternalDriverSessionOptions();
        options.Validate();
        var paths = InternalDriverSessionFactory.ResolvePaths(options);
        return new InternalDriverPreSessionControl(
            paths,
            new InternalDriverPairedTrackerDiscoveryAdapter(
                new PairedLighthouseDeviceDiscovery(
                    new SystemSteamVrHostEnvironment(),
                    new SystemSteamVrFileSystem())),
            new SystemInternalDriverSteamVrProcessInspector(),
            () => InternalDriverRemoval.Create(options));
    }

    public ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(RefreshCoreAsync, cancellationToken);

    public ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
        string leftTrackerSerial,
        string rightTrackerSerial,
        CancellationToken cancellationToken = default)
    {
        var binding = new InternalDriverTrackerBinding(
            leftTrackerSerial,
            rightTrackerSerial);
        return RunSerializedAsync(
            async token =>
            {
                var settings = LoadPreparedSettings();
                InternalDriverSettingsFile.Save(
                    _paths.SettingsPath,
                    settings.WithManualTrackerBinding(binding));
                var refreshed = await RefreshCoreAsync(token).ConfigureAwait(false);
                _snapshot = refreshed with
                {
                    Diagnostic =
                        $"Saved manual tracker binding: left {binding.LeftTrackerSerial}; " +
                        $"right {binding.RightTrackerSerial}. {refreshed.Diagnostic}",
                };
                return _snapshot;
            },
            cancellationToken);
    }

    public ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(
            async token =>
            {
                var settings = LoadPreparedSettings();
                InternalDriverSettingsFile.Save(
                    _paths.SettingsPath,
                    settings.WithManualTrackerBinding(manualTrackerBinding: null));
                var refreshed = await RefreshCoreAsync(token).ConfigureAwait(false);
                _snapshot = refreshed with
                {
                    Diagnostic =
                        "Cleared the manual tracker binding. Motion correlation will use " +
                        $"the existing automatic association behavior. {refreshed.Diagnostic}",
                };
                return _snapshot;
            },
            cancellationToken);

    public ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(
            async token =>
            {
                var settings = LoadPreparedSettings();
                InternalDriverSettingsFile.Save(
                    _paths.SettingsPath,
                    settings.WithUnregisterOnExit(enabled));
                var refreshed = await RefreshCoreAsync(token).ConfigureAwait(false);
                _snapshot = refreshed with
                {
                    Diagnostic = enabled
                        ? "Enabled unregister-on-exit. Controlled Stop or window exit removes " +
                          "receipt-owned driver_ltb registration; the next Start re-registers it " +
                          $"and may require one SteamVR restart. {refreshed.Diagnostic}"
                        : "Disabled unregister-on-exit. Controlled Stop or window exit retains " +
                          $"driver_ltb registration. {refreshed.Diagnostic}",
                };
                return _snapshot;
            },
            cancellationToken);

    public ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(PrepareStartCoreAsync, cancellationToken);

    public ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
        InternalDriverManualBindingVerificationEvidence verification,
        InternalDriverManualBindingDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        return RunSerializedAsync(
            async token =>
            {
                var coreVerification = new ManualTrackerBindingVerificationResult(
                    verification.State switch
                    {
                        InternalDriverManualBindingVerificationState.Agreement =>
                            ManualTrackerBindingVerificationStatus.Agreement,
                        InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate =>
                            ManualTrackerBindingVerificationStatus.MismatchCorrectionCandidate,
                        InternalDriverManualBindingVerificationState.CorrelationFailed =>
                            ManualTrackerBindingVerificationStatus.CorrelationFailed,
                        _ => throw new ArgumentOutOfRangeException(nameof(verification)),
                    },
                    verification.Diagnostic,
                    new ManualTrackerBinding(
                        verification.LeftTrackerSerial,
                        verification.RightTrackerSerial),
                    verification.CorrectionLeftTrackerSerial is { } correctionLeft &&
                    verification.CorrectionRightTrackerSerial is { } correctionRight
                        ? new ManualTrackerBinding(correctionLeft, correctionRight)
                        : null,
                    CorrelationResult: null);
                var selected = coreVerification.SelectBinding(decision switch
                {
                    InternalDriverManualBindingDecision.RetainManualBinding =>
                        ManualTrackerBindingDecision.RetainManualBinding,
                    InternalDriverManualBindingDecision.AcceptCorrectionCandidate =>
                        ManualTrackerBindingDecision.AcceptCorrectionCandidate,
                    _ => throw new ArgumentOutOfRangeException(nameof(decision)),
                });
                var loadedGenerationBefore =
                    InternalDriverSettingsFile.ComputeGeneration(
                        _paths.SettingsPath);
                var settings = LoadPreparedSettings();
                var loadedGenerationAfter =
                    InternalDriverSettingsFile.ComputeGeneration(
                        _paths.SettingsPath);
                if (!string.Equals(
                        loadedGenerationBefore,
                        loadedGenerationAfter,
                        StringComparison.Ordinal))
                {
                    var authoritative = await RefreshCoreAsync(token).ConfigureAwait(false);
                    _snapshot = authoritative with
                    {
                        State = InternalDriverPreSessionState.ManualBindingDecisionStale,
                        Diagnostic =
                            "The authoritative pre-session settings changed while the " +
                            "decision authority was loaded. The pending decision is stale and " +
                            $"no settings were overwritten. Authoritative state was reloaded. " +
                            $"{authoritative.Diagnostic}",
                        Remediation =
                            "Refresh, rerun motion verification for the current pair, then " +
                            "make a new explicit decision.",
                    };
                    return _snapshot;
                }

                if (verification.AuthorityGeneration is { } authorityGeneration &&
                    !string.Equals(
                        authorityGeneration,
                        loadedGenerationAfter,
                        StringComparison.Ordinal))
                {
                    var authoritative = await RefreshCoreAsync(token).ConfigureAwait(false);
                    _snapshot = authoritative with
                    {
                        State = InternalDriverPreSessionState.ManualBindingDecisionStale,
                        Diagnostic =
                            "The authoritative pre-session settings generation changed after " +
                            "motion verification. The pending decision is stale and no settings " +
                            $"were overwritten. Authoritative state was reloaded. " +
                            $"{authoritative.Diagnostic}",
                        Remediation =
                            "Refresh, rerun motion verification for the current pair, then " +
                            "make a new explicit decision.",
                    };
                    return _snapshot;
                }

                if (settings.ManualTrackerBinding is not { } currentBinding ||
                    !string.Equals(
                        currentBinding.LeftTrackerSerial,
                        verification.LeftTrackerSerial,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        currentBinding.RightTrackerSerial,
                        verification.RightTrackerSerial,
                        StringComparison.Ordinal))
                {
                    var authoritative = await RefreshCoreAsync(token).ConfigureAwait(false);
                    _snapshot = authoritative with
                    {
                        State = InternalDriverPreSessionState.ManualBindingDecisionStale,
                        Diagnostic =
                            "The manual tracker binding changed after motion verification. " +
                            "The pending decision is stale and no settings were overwritten. " +
                            $"Authoritative state was reloaded. {authoritative.Diagnostic}",
                        Remediation =
                            "Refresh, rerun motion verification for the current pair, then " +
                            "make a new explicit decision.",
                    };
                    return _snapshot;
                }

                var mutationGeneration = verification.AuthorityGeneration ??
                    loadedGenerationAfter;
                if (!InternalDriverSettingsFile.TrySaveIfGenerationMatches(
                        _paths.SettingsPath,
                        mutationGeneration,
                        settings.WithManualTrackerBinding(
                            new InternalDriverTrackerBinding(
                                selected.LeftTrackerSerial!,
                                selected.RightTrackerSerial!))))
                {
                    var authoritative = await RefreshCoreAsync(token).ConfigureAwait(false);
                    _snapshot = authoritative with
                    {
                        State = InternalDriverPreSessionState.ManualBindingDecisionStale,
                        Diagnostic =
                            "The authoritative pre-session settings changed at the commit " +
                            "boundary. The pending decision is stale and no settings were " +
                            $"overwritten. Authoritative state was reloaded. " +
                            $"{authoritative.Diagnostic}",
                        Remediation =
                            "Refresh, rerun motion verification for the current pair, then " +
                            "make a new explicit decision.",
                    };
                    return _snapshot;
                }

                var refreshed = await RefreshCoreAsync(token).ConfigureAwait(false);
                _snapshot = refreshed with
                {
                    Diagnostic = decision ==
                        InternalDriverManualBindingDecision.AcceptCorrectionCandidate
                            ? $"Accepted the motion-correlation correction explicitly: " +
                              $"left {selected.LeftTrackerSerial}; right " +
                              $"{selected.RightTrackerSerial}. {refreshed.Diagnostic}"
                            : $"Retained the authoritative manual binding explicitly: " +
                              $"left {selected.LeftTrackerSerial}; right " +
                              $"{selected.RightTrackerSerial}. {refreshed.Diagnostic}",
                };
                return _snapshot;
            },
            cancellationToken);
    }

    public ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(CompleteControlledStopCoreAsync, cancellationToken);

    private async ValueTask<InternalDriverPreSessionSnapshot> RefreshCoreAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = LoadPreparedSettings();
            var discovery = await _trackerDiscovery
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            var processes = _processInspector.Inspect();
            SteamVrDriverStartupInspection? registration = null;
            TrackerRoleDrift? trackerRoleDrift = null;
            string? registrationFailure = null;
            try
            {
                await using var maintenance = _maintenanceFactory() ??
                    throw new InvalidOperationException(
                        "The registration-maintenance factory returned null.");
                registration = await maintenance
                    .InspectNextStartAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    trackerRoleDrift = maintenance.InspectTrackerRoleDrift(registration);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    registration = registration with
                    {
                        Diagnostic =
                            $"{registration.Diagnostic} Read-only tracker-role drift " +
                            $"inspection was unavailable ({exception.GetType().Name}); no role " +
                            "was rewritten.",
                    };
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                registrationFailure = exception.Message;
            }

            var trackers = discovery.Devices
                .Select(device => new InternalDriverPairedTrackerOption(
                    device.Serial,
                    device.Model))
                .ToArray();
            var pathEvidence = InspectTrackerPathEvidence();
            var recoveryDiscovery = InspectRecoveryCandidates(registration);
            var storedProfileQuality = AssessStoredProfileQuality(settings);
            var state = discovery.IsSuccess
                ? InternalDriverPreSessionState.Ready
                : InternalDriverPreSessionState.TrackerDiscoveryFailed;
            var diagnostic = discovery.IsSuccess
                ? discovery.Diagnostic
                : $"{discovery.Diagnostic} No exception escaped the typed pairing discovery.";
            if (registrationFailure is not null)
            {
                state = InternalDriverPreSessionState.RegistrationStateRequiresAction;
                diagnostic =
                    $"Next-start driver registration inspection failed: {registrationFailure}";
            }
            else if (registration is not null)
            {
                diagnostic += $" Registration: {registration.Diagnostic}";
                if (RegistrationBlocksStart(registration))
                {
                    state = InternalDriverPreSessionState.RegistrationStateRequiresAction;
                }
            }

            _snapshot = new InternalDriverPreSessionSnapshot(
                state,
                trackers,
                settings.ManualTrackerBinding?.LeftTrackerSerial,
                settings.ManualTrackerBinding?.RightTrackerSerial,
                settings.UnregisterOnExit,
                registration?.State,
                processes,
                diagnostic,
                state == InternalDriverPreSessionState.Ready
                    ? "Select distinct left/right trackers or retain automatic association, " +
                      "then press Start."
                    : "Correct the typed pairing or registration diagnostic while stopped, " +
                      "then refresh.")
            {
                ExternalRegistrationWarnings =
                    registration?.ExternalIntegrationWarnings.ToArray() ?? [],
                RecoveryDiscovery = recoveryDiscovery,
                TrackerRoleDrift = trackerRoleDrift,
                TrackerPathObservations = pathEvidence.Observations,
                TrackerPathReconciliationPending = pathEvidence.Pending,
                TrackerPathEvidenceDiagnostic = pathEvidence.Diagnostic,
                StoredProfileQuality = storedProfileQuality,
            };
            return _snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _snapshot = _snapshot with
            {
                State = InternalDriverPreSessionState.SettingsUnavailable,
                Diagnostic = $"Stopped/pre-session settings could not be loaded: {exception.Message}",
                Remediation =
                    "Repair internal-driver.json while the session is stopped, then refresh.",
            };
            return _snapshot;
        }
    }

    private (
        IReadOnlyList<TrackerPathObservation> Observations,
        bool Pending,
        string Diagnostic) InspectTrackerPathEvidence()
    {
        try
        {
            var store = new TrackerPathObservationStore(
                _paths.EffectiveTrackerPathObservationStorePath);
            if (store.HasPendingReconciliation)
            {
                var lastCommitted = store.LoadLastCommittedForPresentation().ToArray();
                return (
                    lastCommitted,
                    true,
                    "A tracker-path change is pending reconciliation. The displayed current " +
                    "and history values are the last committed observations, not current " +
                    "mutation authority, until one normal live session refreshes them.");
            }

            var observations = store.LoadAll().ToArray();
            return observations.Length == 0
                ? (
                    observations,
                    false,
                    "No live tracker-path observation is stored. Run one normal live session " +
                    "to capture exact registered paths.")
                : (
                    observations,
                    false,
                    $"Loaded {observations.Length} exact live tracker-path observation(s). " +
                    "Run one normal live session after hardware or path changes to refresh them.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                Array.Empty<TrackerPathObservation>(),
                false,
                $"Tracker-path evidence is unavailable ({exception.GetType().Name}). " +
                "Run one normal live session after repairing the evidence store.");
        }
    }

    private static SteamVrSettingsRecoveryDiscovery? InspectRecoveryCandidates(
        SteamVrDriverStartupInspection? registration)
    {
        if (registration is null)
        {
            return null;
        }

        try
        {
            return new SteamVrSettingsManager(registration.Paths.SettingsFile)
                .DiscoverRecoveryBackups();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private StoredCalibrationProfilePairAssessment? AssessStoredProfileQuality(
        InternalDriverSettings settings)
    {
        if (settings.ManualTrackerBinding is not { } binding)
        {
            return null;
        }

        try
        {
            var calibration = new InternalDriverCalibration(
                _paths.CalibrationProfileStorePath);
            var left = calibration.FindReusableProfile(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Left,
                    binding.LeftTrackerSerial,
                    ControllerModel));
            var right = calibration.FindReusableProfile(
                new InternalDriverCalibrationContext(
                    MetaLinkHand.Right,
                    binding.RightTrackerSerial,
                    ControllerModel));
            return left.CanReuse && right.CanReuse
                ? StoredCalibrationProfileQualityAssessor.AssessPair(
                    left.Profile!,
                    right.Profile!)
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async ValueTask<InternalDriverPreSessionSnapshot> PrepareStartCoreAsync(
        CancellationToken cancellationToken)
    {
        var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        if (refreshed.State ==
            InternalDriverPreSessionState.RegistrationStateRequiresAction)
        {
            return refreshed;
        }

        if (refreshed.RegistrationState == SteamVrDriverStartupState.DuplicateRegistrations)
        {
            try
            {
                await using var maintenance = _maintenanceFactory() ??
                    throw new InvalidOperationException(
                        "The registration-maintenance factory returned null.");
                var inspection = await maintenance
                    .InspectNextStartAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!inspection.CanRemoveAutomatically)
                {
                    _snapshot = refreshed with
                    {
                        State = InternalDriverPreSessionState.RegistrationStateRequiresAction,
                        Diagnostic =
                            $"Duplicate driver_ltb registrations cannot be removed " +
                            $"automatically: {inspection.Diagnostic}",
                        Remediation =
                            "Inspect the independently owned roots and receipts. Unrelated " +
                            "external drivers were not modified.",
                    };
                    return _snapshot;
                }

                var removal = await maintenance
                    .RemoveAsync(cancellationToken)
                    .ConfigureAwait(false);
                var duplicateCleanupProcesses = _processInspector.Inspect();
                _snapshot = refreshed with
                {
                    State = InternalDriverPreSessionState.RestartRequired,
                    SteamVrProcesses = duplicateCleanupProcesses,
                    Diagnostic =
                        $"Next-start inspection found independently owned duplicate " +
                        $"driver_ltb registrations and removed their exact roots " +
                        $"transactionally. {removal.Diagnostic} " +
                        "SteamVR must restart before Start proceeds; no unrelated external " +
                        "driver was modified.",
                    Remediation =
                        "Restart SteamVR once, then refresh and press Start again.",
                };
                return _snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _snapshot = refreshed with
                {
                    State = InternalDriverPreSessionState.RegistrationStateRequiresAction,
                    Diagnostic =
                        $"Automatic exact-root duplicate cleanup failed closed: " +
                        $"{exception.Message}",
                    Remediation =
                        "Inspect the durable receipts and exact registered roots. Unrelated " +
                        "external drivers were not modified.",
                };
                return _snapshot;
            }
        }

        if (!refreshed.HasManualBinding &&
            refreshed.State is InternalDriverPreSessionState.Ready or
                InternalDriverPreSessionState.TrackerDiscoveryFailed)
        {
            _snapshot = refreshed with
            {
                State = InternalDriverPreSessionState.Ready,
                Diagnostic =
                    "No manual tracker binding is configured. Existing automatic " +
                    "motion-correlation association behavior remains authoritative. " +
                    $"Stopped paired-tracker discovery diagnostic: {refreshed.Diagnostic}",
                Remediation = refreshed.State ==
                    InternalDriverPreSessionState.TrackerDiscoveryFailed
                        ? "Start may proceed with automatic association. Correct the typed " +
                          "paired-tracker discovery diagnostic before selecting a manual pair."
                        : "Start may proceed.",
            };
            return _snapshot;
        }

        if (refreshed.State != InternalDriverPreSessionState.Ready)
        {
            return refreshed;
        }

        var processes = _processInspector.Inspect();
        if (processes.IsAnyRunning)
        {
            _snapshot = refreshed with
            {
                State = InternalDriverPreSessionState.SteamVrMustBeStopped,
                SteamVrProcesses = processes,
                Diagnostic =
                    $"Manual-binding preflight refused because these SteamVR processes are " +
                    $"running: {processes.RunningProcessList}. No SteamVR settings were written.",
                Remediation =
                    "Exit SteamVR completely so both vrserver and vrmonitor are stopped, " +
                    "then press Start again.",
            };
            return _snapshot;
        }

        var selected = new[]
        {
            refreshed.LeftTrackerSerial!,
            refreshed.RightTrackerSerial!,
        };
        var missing = selected
            .Where(serial => !refreshed.PairedTrackers.Any(option =>
                string.Equals(option.Serial, serial, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length != 0)
        {
            _snapshot = refreshed with
            {
                State = InternalDriverPreSessionState.TrackerBindingIncomplete,
                Diagnostic =
                    $"The saved manual binding references tracker(s) not present in paired " +
                    $"Lighthouse config: {string.Join(", ", missing)}. No settings were written.",
                Remediation =
                    "Pair the missing tracker or select a complete distinct left/right pair.",
            };
            return _snapshot;
        }

        try
        {
            var store = new TrackerPathObservationStore(
                _paths.EffectiveTrackerPathObservationStorePath);
            var storedSnapshot = store.LoadAll();
            var observations = selected
                .Select(serial => storedSnapshot.SingleOrDefault(observation =>
                    string.Equals(
                        observation.TrackerSerial,
                        serial,
                        StringComparison.Ordinal)))
                .ToArray();
            if (observations.Any(observation => observation is null) ||
                observations.Select(observation => observation!.RegisteredDevicePath)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != 2)
            {
                return RegisteredDevicePathUnresolved(
                    "No complete distinct exact current tracker-path evidence pair is available.");
            }

            _snapshot = refreshed with
            {
                State = InternalDriverPreSessionState.Ready,
                Diagnostic =
                    "Manual-binding preflight resolved two distinct exact registered-device " +
                    "paths from durable live-session evidence. The path values remain redacted; " +
                    "no SteamVR settings write was attempted.",
                Remediation =
                    "Start may proceed with the saved manual pair. TrackerRole_None hardware " +
                    "behavior remains unchecked on the target Windows SteamVR runtime.",
            };
            return _snapshot;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return RegisteredDevicePathUnresolved(
                $"Stored tracker-path evidence failed closed ({exception.GetType().Name}).");
        }

        InternalDriverPreSessionSnapshot RegisteredDevicePathUnresolved(string reason)
        {
            _snapshot = refreshed with
            {
                State = InternalDriverPreSessionState.RegisteredDevicePathUnresolved,
                Diagnostic =
                    "Manual-binding preflight is blocked before tracker-role neutralization. " +
                    $"{reason} Paired Lighthouse config remains serial/model evidence only. " +
                    "No steamvr.vrsettings write was attempted, no tracker path was " +
                    "synthesized, and stored path values remain redacted.",
                Remediation =
                    "Clear or disable the manual binding if automatic association is desired; " +
                    "otherwise complete one normal live LTB session so real OpenVR enumeration " +
                    "records the selected pair, then retry while SteamVR is stopped.",
            };
            return _snapshot;
        }
    }

    private async ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopCoreAsync(
        CancellationToken cancellationToken)
    {
        InternalDriverSettings settings;
        try
        {
            settings = LoadPreparedSettings();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _snapshot = _snapshot with
            {
                State = InternalDriverPreSessionState.Faulted,
                Diagnostic =
                    $"Controlled-stop policy could not load internal-driver settings: " +
                    $"{exception.Message}",
                Remediation =
                    "Inspect internal-driver.json and the registration receipt before exiting.",
            };
            return _snapshot;
        }

        if (!settings.UnregisterOnExit)
        {
            _snapshot = _snapshot with
            {
                State = InternalDriverPreSessionState.CleanupSkipped,
                UnregisterOnExit = false,
                Diagnostic =
                    "Controlled Stop completed with unregister-on-exit disabled; " +
                    "driver_ltb registration was retained.",
                Remediation =
                    "Enable unregister-on-exit if controlled Stop or window exit should remove it.",
            };
            return _snapshot;
        }

        try
        {
            await using var maintenance = _maintenanceFactory() ??
                throw new InvalidOperationException(
                    "The registration-maintenance factory returned null.");
            var inspection = await maintenance
                .InspectNextStartAsync(cancellationToken)
                .ConfigureAwait(false);
            if (RegistrationBlocksCleanup(inspection))
            {
                _snapshot = _snapshot with
                {
                    State = InternalDriverPreSessionState.Faulted,
                    RegistrationState = inspection.State,
                    Diagnostic =
                        $"Unregister-on-exit refused because exact LTB ownership is not safe: " +
                        $"{inspection.Diagnostic}",
                    Remediation =
                        "Inspect the durable receipts and exact registered roots. Unrelated " +
                        "external drivers were not modified.",
                };
                return _snapshot;
            }

            var removal = await maintenance
                .RemoveAsync(cancellationToken)
                .ConfigureAwait(false);
            var processes = _processInspector.Inspect();
            var runningNotice = processes.IsAnyRunning
                ? " SteamVR is still running, so registration removal takes effect only after " +
                  "SteamVR restarts; this does not remove already-published devices live."
                : " The next Start re-registers driver_ltb and may require one SteamVR restart.";
            _snapshot = _snapshot with
            {
                State = removal.RestartRequired
                    ? InternalDriverPreSessionState.RestartRequired
                    : InternalDriverPreSessionState.CleanupCompleted,
                RegistrationState = inspection.State,
                SteamVrProcesses = processes,
                UnregisterOnExit = true,
                Diagnostic = removal.Diagnostic + runningNotice,
                Remediation = processes.IsAnyRunning
                    ? "Restart SteamVR before evaluating the registration or device list."
                    : "No cleanup remediation is required.",
            };
            return _snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _snapshot = _snapshot with
            {
                State = InternalDriverPreSessionState.Faulted,
                Diagnostic = $"Unregister-on-exit failed closed: {exception.Message}",
                Remediation =
                    "Inspect the durable registration receipt and exact OpenVR driver roots. " +
                    "Unrelated external drivers were not modified.",
            };
            return _snapshot;
        }
    }

    private InternalDriverSettings LoadPreparedSettings()
    {
        _ = ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(_paths);
        return InternalDriverSettingsFile.Load(_paths.SettingsPath);
    }

    private static bool RegistrationBlocksStart(
        SteamVrDriverStartupInspection inspection) =>
        inspection.State is SteamVrDriverStartupState.StaleReceiptRegistrationMismatch or
            SteamVrDriverStartupState.AmbiguousNonCanonicalRegistration ||
        inspection.State == SteamVrDriverStartupState.DuplicateRegistrations &&
            !inspection.CanRemoveAutomatically;

    private static bool RegistrationBlocksCleanup(
        SteamVrDriverStartupInspection inspection) =>
        inspection.State is SteamVrDriverStartupState.StaleReceiptRegistrationMismatch or
            SteamVrDriverStartupState.AmbiguousNonCanonicalRegistration ||
        inspection.State == SteamVrDriverStartupState.DuplicateRegistrations &&
            !inspection.CanRemoveAutomatically;

    private async ValueTask<InternalDriverPreSessionSnapshot> RunSerializedAsync(
        Func<CancellationToken, ValueTask<InternalDriverPreSessionSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _operationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
