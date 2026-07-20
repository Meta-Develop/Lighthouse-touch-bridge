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
/// Durable authority for LTB-issued registration receipts. A lifecycle saves
/// the receipt of every registration it owns and deletes it after a completed
/// removal, so removal stays possible after an application restart. Receipts
/// are keyed by the exact canonical driver root.
/// </summary>
public interface ISteamVrDriverReceiptStore
{
    SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot);

    void Save(SteamVrDriverRegistrationReceipt receipt);

    void Delete(string canonicalDriverRoot);
}

/// <summary>
/// Non-persisting store: removal authority then lives only in process memory,
/// which keeps the historical single-process lifecycle behavior.
/// </summary>
public sealed class NullSteamVrDriverReceiptStore : ISteamVrDriverReceiptStore
{
    public static NullSteamVrDriverReceiptStore Instance { get; } = new();

    public SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot) => null;

    public void Save(SteamVrDriverRegistrationReceipt receipt)
    {
    }

    public void Delete(string canonicalDriverRoot)
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
