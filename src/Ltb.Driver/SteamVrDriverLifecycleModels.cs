namespace Ltb.Driver;

public enum SteamVrDriverReadiness
{
    NotRegistered = 0,
    RestartRequired,
    RuntimeVerificationRequired,
}

public enum SteamVrActivateMultipleDriversState
{
    Absent = 0,
    Disabled,
    Enabled,
}

/// <summary>
/// Read-only classification of LTB registration ownership at application
/// startup. External drivers without either an exact durable LTB receipt or
/// LTB's complete staged artifact identity remain unrelated and never grant
/// removal authority.
/// </summary>
public enum SteamVrDriverStartupState
{
    NoLtbRegistration = 0,
    ReceiptOwnedRegistration,
    ReceiptOnlyNoRegistration,
    ReceiptlessArtifactProvenRegistration,
    StaleReceiptRegistrationMismatch,
    DuplicateRegistrations,
    AmbiguousNonCanonicalRegistration,
}

public enum SteamVrDriverDiagnosticCode
{
    PlatformUnsupported = 0,
    LocalApplicationDataUnavailable,
    OpenVrPathsMissing,
    OpenVrPathsInvalid,
    VrPathRegMissing,
    SteamVrSettingsMissing,
    StagedManifestMissing,
    StagedBinaryMissing,
    ProcessFailed,
    RegistrationVerificationFailed,
    SettingsInvalid,
    ConcurrentModification,
    RollbackFailed,
    RemovalOwnershipLost,
    StagedBuildIdMissing,
    StagedBuildIdInvalid,
}

public sealed record SteamVrPaths(
    string OpenVrPathsFile,
    string RuntimeRoot,
    string ConfigRoot,
    string VrPathRegExecutable,
    string SettingsFile);

public sealed record SteamVrDriverInspection(
    SteamVrPaths Paths,
    string CanonicalDriverRoot,
    string StagedBuildId,
    bool IsRegistered,
    SteamVrActivateMultipleDriversState ActivateMultipleDrivers);

/// <summary>
/// Read-only next-start evidence across the complete OpenVR external-driver
/// registry and every durable LTB receipt.
/// </summary>
public sealed record SteamVrDriverStartupInspection(
    SteamVrPaths Paths,
    string CanonicalStagedDriverRoot,
    string StagedBuildId,
    SteamVrDriverStartupState State,
    SteamVrActivateMultipleDriversState ActivateMultipleDrivers,
    IReadOnlyList<string> CanonicalLtbDriverRoots,
    IReadOnlyList<string> UnrelatedExternalDriverRoots,
    IReadOnlyList<SteamVrDriverRegistrationReceipt> DurableReceipts,
    SteamVrDriverRegistrationReceipt? MatchingReceipt,
    bool CanRemoveAutomatically,
    string Diagnostic);

public sealed record SteamVrDriverRegistrationReceipt(
    string CanonicalDriverRoot,
    SteamVrActivateMultipleDriversState PriorActivateMultipleDrivers,
    bool ActivateMultipleDriversChanged,
    bool SteamVrSectionWasPresent,
    Guid OwnershipToken);

public sealed record SteamVrDriverLifecycleResult(
    bool Changed,
    bool RestartRequired,
    SteamVrDriverReadiness Readiness,
    string Diagnostic,
    SteamVrPaths Paths,
    SteamVrDriverRegistrationReceipt Receipt);

/// <summary>
/// Result of one exact-root cleanup transaction. Every root was independently
/// authorized before mutation and all unrelated registrations retained their
/// original order.
/// </summary>
public sealed record SteamVrDriverCleanupResult(
    bool Changed,
    bool RestartRequired,
    SteamVrDriverReadiness Readiness,
    string Diagnostic,
    SteamVrPaths Paths,
    IReadOnlyList<string> CanonicalDriverRoots);

/// <summary>
/// Durable authority for LTB-issued registration receipts. A lifecycle saves
/// the receipt of every registration it owns and deletes it after a completed
/// removal, so removal stays possible after an application restart. Receipts
/// are keyed by the exact canonical driver root.
/// </summary>
public interface ISteamVrDriverReceiptStore
{
    SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot);

    /// <summary>
    /// Loads all durable receipts so a next-start inspection can detect
    /// receipts for relocated or otherwise stale driver roots.
    /// </summary>
    IReadOnlyList<SteamVrDriverRegistrationReceipt> LoadAll();

    void Save(SteamVrDriverRegistrationReceipt receipt);

    void SaveAll(IReadOnlyList<SteamVrDriverRegistrationReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        foreach (var receipt in receipts)
        {
            Save(receipt);
        }
    }

    void Delete(string canonicalDriverRoot);

    void DeleteAll(IReadOnlyList<string> canonicalDriverRoots)
    {
        ArgumentNullException.ThrowIfNull(canonicalDriverRoots);
        foreach (var canonicalDriverRoot in canonicalDriverRoots)
        {
            Delete(canonicalDriverRoot);
        }
    }
}

/// <summary>
/// Non-persisting store: removal authority then lives only in process memory,
/// which keeps the historical single-process lifecycle behavior.
/// </summary>
public sealed class NullSteamVrDriverReceiptStore : ISteamVrDriverReceiptStore
{
    public static NullSteamVrDriverReceiptStore Instance { get; } = new();

    public SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot) => null;

    public IReadOnlyList<SteamVrDriverRegistrationReceipt> LoadAll() => [];

    public void Save(SteamVrDriverRegistrationReceipt receipt)
    {
    }

    public void SaveAll(IReadOnlyList<SteamVrDriverRegistrationReceipt> receipts)
    {
    }

    public void Delete(string canonicalDriverRoot)
    {
    }

    public void DeleteAll(IReadOnlyList<string> canonicalDriverRoots)
    {
    }
}

public sealed class SteamVrDriverLifecycleException : Exception
{
    public SteamVrDriverLifecycleException(
        SteamVrDriverDiagnosticCode diagnosticCode,
        string message)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
    }

    public SteamVrDriverLifecycleException(
        SteamVrDriverDiagnosticCode diagnosticCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public SteamVrDriverDiagnosticCode DiagnosticCode { get; }
}
