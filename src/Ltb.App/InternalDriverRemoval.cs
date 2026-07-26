using Ltb.Configuration;
using Ltb.Driver;

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

    public void Delete(string canonicalDriverRoot) => _store.Delete(canonicalDriverRoot);

    public void DeleteAll(IReadOnlyList<string> canonicalDriverRoots) =>
        _store.DeleteAll(canonicalDriverRoots);

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
        record.OwnershipToken);

    private static DriverRegistrationReceiptRecord ToStoredRecord(
        SteamVrDriverRegistrationReceipt receipt) => new(
        receipt.CanonicalDriverRoot,
        ToStoredState(receipt.PriorActivateMultipleDrivers),
        receipt.ActivateMultipleDriversChanged,
        receipt.SteamVrSectionWasPresent,
        receipt.OwnershipToken);
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

    internal InternalDriverRemoval(
        ISteamVrDriverLifecycle lifecycle,
        ISteamVrDriverReceiptStore receiptStore,
        string stagedDriverRoot)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDriverRoot);
        _stagedDriverRoot = stagedDriverRoot;
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
            paths.StagedDriverRoot);
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
            var adopted = new SteamVrDriverRegistrationReceipt(
                canonicalRoot,
                inspection.ActivateMultipleDrivers,
                ActivateMultipleDriversChanged: false,
                SteamVrSectionWasPresent: true,
                Guid.NewGuid());
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

    public ValueTask DisposeAsync()
    {
        _lifecycle.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SteamVrDriverLifecycleException OwnershipLost(string message) =>
        new(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, message);
}
