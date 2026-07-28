using Ltb.Configuration;
using Ltb.Driver;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>The outcome of one transactional first-party driver removal.</summary>
public sealed record InternalDriverRemovalResult(
    bool Changed,
    bool RestartRequired,
    string Diagnostic);

/// <summary>User-facing removal of the registered first-party driver.</summary>
public interface IInternalDriverRemover : IAsyncDisposable
{
    ValueTask<InternalDriverRemovalResult> RemoveAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit awaited maintenance seam for later next-start diagnostics and
/// default-on-exit unregistering. Disposal does not perform asynchronous
/// cleanup; callers choose and await <see cref="IInternalDriverRemover.RemoveAsync"/>.
/// </summary>
public interface IInternalDriverRegistrationMaintenance : IInternalDriverRemover
{
    ValueTask<SteamVrDriverStartupInspection> InspectNextStartAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads role drift only for the exact tracker paths retained in LTB's
    /// durable neutralization receipt. Implementations without that capability
    /// return no evidence and never synthesize paths.
    /// </summary>
    TrackerRoleDrift? InspectTrackerRoleDrift(
        SteamVrDriverStartupInspection inspection) => null;
}

/// <summary>
/// Adapts the durable <see cref="DriverRegistrationReceiptStore"/> to the
/// <c>Ltb.Driver</c> receipt-store boundary so registration receipts survive
/// application restarts without <c>Ltb.Driver</c> or <c>Ltb.Configuration</c>
/// referencing each other.
/// </summary>
public sealed class ConfigurationSteamVrDriverReceiptStore : ISteamVrDriverReceiptStore
{
    private readonly DriverRegistrationReceiptStore _store;

    public ConfigurationSteamVrDriverReceiptStore(string path)
        : this(new DriverRegistrationReceiptStore(path))
    {
    }

    public ConfigurationSteamVrDriverReceiptStore(DriverRegistrationReceiptStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot)
    {
        var record = _store.TryLoad(canonicalDriverRoot);
        return record is null ? null : FromStoredRecord(record);
    }

    public IReadOnlyList<SteamVrDriverRegistrationReceipt> LoadAll() =>
        _store.LoadAll().Select(FromStoredRecord).ToArray();

    public void Save(SteamVrDriverRegistrationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        SaveAll([receipt]);
    }

    public void SaveAll(IReadOnlyList<SteamVrDriverRegistrationReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        _store.SaveAll(receipts.Select(ToStoredRecord).ToArray());
    }

    /// <summary>
    /// Compatibility-only root deletion. Authority-sensitive lifecycle paths
    /// use the complete expected-receipt overload below.
    /// </summary>
    public void Delete(string canonicalDriverRoot) => _store.Delete(canonicalDriverRoot);

    /// <summary>Compatibility-only root batch deletion.</summary>
    public void DeleteAll(IReadOnlyList<string> canonicalDriverRoots) =>
        _store.DeleteAll(canonicalDriverRoots);

    public bool Delete(SteamVrDriverRegistrationReceipt expectedReceipt)
    {
        ArgumentNullException.ThrowIfNull(expectedReceipt);
        return _store.Delete(ToStoredRecord(expectedReceipt));
    }

    public int DeleteAll(
        IReadOnlyList<SteamVrDriverRegistrationReceipt> expectedReceipts)
    {
        ArgumentNullException.ThrowIfNull(expectedReceipts);
        return _store.DeleteAll(expectedReceipts.Select(ToStoredRecord).ToArray());
    }

    private static string ToStoredState(SteamVrActivateMultipleDriversState state) => state switch
    {
        SteamVrActivateMultipleDriversState.Absent =>
            DriverRegistrationReceiptSchema.PriorStateAbsent,
        SteamVrActivateMultipleDriversState.Disabled =>
            DriverRegistrationReceiptSchema.PriorStateDisabled,
        SteamVrActivateMultipleDriversState.Enabled =>
            DriverRegistrationReceiptSchema.PriorStateEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static SteamVrActivateMultipleDriversState FromStoredState(string state) => state switch
    {
        DriverRegistrationReceiptSchema.PriorStateAbsent =>
            SteamVrActivateMultipleDriversState.Absent,
        DriverRegistrationReceiptSchema.PriorStateDisabled =>
            SteamVrActivateMultipleDriversState.Disabled,
        DriverRegistrationReceiptSchema.PriorStateEnabled =>
            SteamVrActivateMultipleDriversState.Enabled,
        _ => throw new InvalidDataException(
            $"Stored prior activateMultipleDrivers state '{state}' is not recognized."),
    };

    private static SteamVrDriverRegistrationReceipt FromStoredRecord(
        DriverRegistrationReceiptRecord record) => new(
        record.CanonicalDriverRoot,
        FromStoredState(record.PriorActivateMultipleDrivers),
        record.ActivateMultipleDriversChanged,
        record.SteamVrSectionWasPresent,
        record.OwnershipToken,
        record.BuildId is null
            ? null
            : new SteamVrDriverArtifactIdentity(
                record.BuildId,
                record.ManifestSha256!,
                record.BinarySha256!,
                record.BuildIdSha256!));

    private static DriverRegistrationReceiptRecord ToStoredRecord(
        SteamVrDriverRegistrationReceipt receipt) => new(
        receipt.CanonicalDriverRoot,
        ToStoredState(receipt.PriorActivateMultipleDrivers),
        receipt.ActivateMultipleDriversChanged,
        receipt.SteamVrSectionWasPresent,
        receipt.OwnershipToken,
        receipt.ArtifactIdentity?.BuildId,
        receipt.ArtifactIdentity?.ManifestSha256,
        receipt.ArtifactIdentity?.BinarySha256,
        receipt.ArtifactIdentity?.BuildIdSha256);
}

/// <summary>
/// Zero-input, restart-safe removal of the first-party driver registration.
/// Ownership comes from the durable registration receipt; a receiptless but
/// currently registered LTB driver is first adopted with a conservative
/// receipt (settings deliberately left untouched) after the staged driver
/// artifacts prove the canonical root is LTB's own driver directory. Unrelated
/// drivers are never modified.
/// </summary>
public sealed class InternalDriverRemoval : IInternalDriverRegistrationMaintenance
{
    private readonly ISteamVrDriverLifecycle _lifecycle;
    private readonly ISteamVrDriverReceiptStore _receiptStore;
    private readonly string _stagedDriverRoot;
    private readonly string? _trackerRoleReceiptPath;

    internal InternalDriverRemoval(
        ISteamVrDriverLifecycle lifecycle,
        ISteamVrDriverReceiptStore receiptStore,
        string stagedDriverRoot,
        string? trackerRoleReceiptPath = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDriverRoot);
        _stagedDriverRoot = stagedDriverRoot;
        _trackerRoleReceiptPath = trackerRoleReceiptPath is null
            ? null
            : Path.GetFullPath(trackerRoleReceiptPath);
    }

    public static InternalDriverRemoval Create(InternalDriverSessionOptions? options = null)
    {
        options ??= new InternalDriverSessionOptions();
        options.Validate();
        var paths = InternalDriverSessionFactory.ResolvePaths(options);
        var receiptStore = new ConfigurationSteamVrDriverReceiptStore(
            paths.DriverReceiptStorePath);
        return new InternalDriverRemoval(
            SteamVrDriverLifecycle.CreateDefault(receiptStore),
            receiptStore,
            paths.StagedDriverRoot,
            Path.Combine(
                Path.GetDirectoryName(paths.DriverReceiptStorePath)
                    ?? throw new InvalidOperationException(
                        "The driver receipt store must have a parent directory."),
                "tracker-role-recovery.json"));
    }

    public async ValueTask<InternalDriverRemovalResult> RemoveAsync(
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectNextStartAsync(
            cancellationToken).ConfigureAwait(false);
        if (inspection.State == SteamVrDriverStartupState.NoLtbRegistration)
        {
            return new InternalDriverRemovalResult(
                Changed: false,
                RestartRequired: false,
                "driver_ltb is not registered and no LTB registration receipt exists; " +
                "there is nothing to remove.");
        }

        if (!inspection.CanRemoveAutomatically)
        {
            throw OwnershipLost(
                "The inspected LTB state is not eligible for automatic exact-root removal.");
        }

        var receipts = inspection.DurableReceipts.ToList();
        var adoptedReceipts = new List<SteamVrDriverRegistrationReceipt>();
        foreach (var canonicalRoot in inspection.CanonicalLtbDriverRoots)
        {
            if (receipts.Any(receipt => string.Equals(
                    receipt.CanonicalDriverRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Startup inspection has proven the manifest identity, binary
            // layout, build identity, and canonical root. Without a
            // pre-registration snapshot this conservative adoption deliberately
            // claims no activateMultipleDrivers restoration authority.
            var artifactEvidence = inspection.ReceiptlessRegistrationArtifactEvidence
                .SingleOrDefault(evidence => string.Equals(
                    evidence.CanonicalDriverRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase));
            if (artifactEvidence is null)
            {
                throw OwnershipLost(
                    $"Startup inspection did not provide byte-exact artifact evidence for " +
                    $"receiptless LTB root '{canonicalRoot}'.");
            }

            var adopted = new SteamVrDriverRegistrationReceipt(
                canonicalRoot,
                inspection.ActivateMultipleDrivers,
                ActivateMultipleDriversChanged: false,
                SteamVrSectionWasPresent: true,
                Guid.NewGuid(),
                artifactEvidence.ArtifactIdentity);
            receipts.Add(adopted);
            adoptedReceipts.Add(adopted);
        }

        if (receipts.Count == 0)
        {
            throw OwnershipLost(
                "Automatic cleanup did not produce any exact-root LTB removal authority.");
        }

        if (adoptedReceipts.Count > 0)
        {
            _receiptStore.SaveAll(adoptedReceipts);
        }

        var result = await _lifecycle.RemoveOwnedAsync(
            receipts,
            cancellationToken).ConfigureAwait(false);
        return new InternalDriverRemovalResult(
            result.Changed,
            result.RestartRequired,
            result.Diagnostic);
    }

    public ValueTask<SteamVrDriverStartupInspection> InspectNextStartAsync(
        CancellationToken cancellationToken = default) =>
        _lifecycle.InspectStartupAsync(
            _stagedDriverRoot,
            cancellationToken);

    public TrackerRoleDrift? InspectTrackerRoleDrift(
        SteamVrDriverStartupInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return _trackerRoleReceiptPath is null
            ? null
            : SteamVrSettingsTrackerNeutralizationBackend.InspectRetainedRoleDrift(
                _trackerRoleReceiptPath,
                inspection.Paths.SettingsFile);
    }

    public ValueTask DisposeAsync()
    {
        _lifecycle.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SteamVrDriverLifecycleException OwnershipLost(string message) =>
        new(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, message);
}
