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
        return record is null
            ? null
            : new SteamVrDriverRegistrationReceipt(
                record.CanonicalDriverRoot,
                FromStoredState(record.PriorActivateMultipleDrivers),
                record.ActivateMultipleDriversChanged,
                record.SteamVrSectionWasPresent,
                record.OwnershipToken);
    }

    public void Save(SteamVrDriverRegistrationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _store.Save(new DriverRegistrationReceiptRecord(
            receipt.CanonicalDriverRoot,
            ToStoredState(receipt.PriorActivateMultipleDrivers),
            receipt.ActivateMultipleDriversChanged,
            receipt.SteamVrSectionWasPresent,
            receipt.OwnershipToken));
    }

    public void Delete(string canonicalDriverRoot) => _store.Delete(canonicalDriverRoot);

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
}

/// <summary>
/// Zero-input, restart-safe removal of the first-party driver registration.
/// Ownership comes from the durable registration receipt; a receiptless but
/// currently registered LTB driver is first adopted with a conservative
/// receipt (settings deliberately left untouched) after the staged driver
/// artifacts prove the canonical root is LTB's own driver directory. Unrelated
/// drivers are never modified.
/// </summary>
public sealed class InternalDriverRemoval : IInternalDriverRemover
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
        var inspection = await _lifecycle.InspectAsync(
            _stagedDriverRoot,
            cancellationToken).ConfigureAwait(false);
        var receipt = _receiptStore.TryLoad(inspection.CanonicalDriverRoot);
        if (receipt is null)
        {
            if (!inspection.IsRegistered)
            {
                return new InternalDriverRemovalResult(
                    Changed: false,
                    RestartRequired: false,
                    "driver_ltb is not registered and no LTB registration receipt exists; " +
                    "there is nothing to remove.");
            }

            // The registration predates durable receipts. InspectAsync has
            // already proven the canonical root is LTB's own staged driver
            // directory, so adopt it; without a pre-registration snapshot the
            // activateMultipleDrivers setting is deliberately left unchanged.
            receipt = new SteamVrDriverRegistrationReceipt(
                inspection.CanonicalDriverRoot,
                inspection.ActivateMultipleDrivers,
                ActivateMultipleDriversChanged: false,
                SteamVrSectionWasPresent: true,
                Guid.NewGuid());
            _receiptStore.Save(receipt);
        }

        var result = await _lifecycle.RemoveAsync(
            receipt,
            cancellationToken).ConfigureAwait(false);
        return new InternalDriverRemovalResult(
            result.Changed,
            result.RestartRequired,
            result.Diagnostic);
    }

    public ValueTask DisposeAsync()
    {
        _lifecycle.Dispose();
        return ValueTask.CompletedTask;
    }
}
