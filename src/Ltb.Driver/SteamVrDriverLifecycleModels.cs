using System.Text.RegularExpressions;

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
    StagedManifestInvalid,
    StagedBinaryInvalid,
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
    string Diagnostic)
{
    /// <summary>
    /// Observational warnings for recognized unrelated registrations. These
    /// warnings do not grant removal authority or claim runtime activity.
    /// </summary>
    public IReadOnlyList<ExternalSteamVrIntegrationWarning> ExternalIntegrationWarnings
    {
        get;
        init;
    } = [];

    /// <summary>
    /// Byte-exact artifact evidence for each receiptless registration that
    /// startup inspection proved as LTB-owned. This evidence is observational
    /// until the App adapter persists it in a new conservative receipt; the
    /// lifecycle revalidates it before any removal mutation.
    /// </summary>
    public IReadOnlyList<SteamVrDriverRegistrationArtifactEvidence>
        ReceiptlessRegistrationArtifactEvidence
    {
        get;
        init;
    } = [];
}

/// <summary>
/// Validated build identity plus exact SHA-256 identities captured from the
/// three authority-bearing driver artifact byte sequences.
/// </summary>
public sealed record SteamVrDriverArtifactIdentity(
    string BuildId,
    string ManifestSha256,
    string BinarySha256,
    string BuildIdSha256)
{
    private static readonly Regex BuildIdPattern = new(
        @"\Adriver_ltb-[0-9]+\.[0-9]+\.[0-9]+-ipc-[0-9]+\.[0-9]+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string BuildId { get; } = RequireBuildId(BuildId);

    public string ManifestSha256 { get; } =
        NormalizeSha256(ManifestSha256, nameof(ManifestSha256));

    public string BinarySha256 { get; } =
        NormalizeSha256(BinarySha256, nameof(BinarySha256));

    public string BuildIdSha256 { get; } =
        NormalizeSha256(BuildIdSha256, nameof(BuildIdSha256));

    private static string RequireBuildId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return BuildIdPattern.IsMatch(value)
            ? value
            : throw new ArgumentException(
                "The driver build identity is blank or malformed.",
                nameof(value));
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The artifact identity must be a 64-character SHA-256 value.",
                parameterName);
        }

        return value.ToLowerInvariant();
    }
}

public sealed record SteamVrDriverRegistrationArtifactEvidence(
    string CanonicalDriverRoot,
    SteamVrDriverArtifactIdentity ArtifactIdentity);

public sealed record SteamVrDriverRegistrationReceipt(
    string CanonicalDriverRoot,
    SteamVrActivateMultipleDriversState PriorActivateMultipleDrivers,
    bool ActivateMultipleDriversChanged,
    bool SteamVrSectionWasPresent,
    Guid OwnershipToken,
    SteamVrDriverArtifactIdentity? ArtifactIdentity = null);

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

    /// <summary>
    /// Deletes only the complete expected receipt generation. Implementations
    /// that provide only compatibility root deletion fail closed here.
    /// </summary>
    bool Delete(SteamVrDriverRegistrationReceipt expectedReceipt) =>
        throw new NotSupportedException(
            "This receipt store does not provide conditional expected-record deletion.");

    /// <summary>
    /// Deletes a complete expected receipt set atomically. Implementations
    /// that provide only compatibility root deletion fail closed here.
    /// </summary>
    int DeleteAll(IReadOnlyList<SteamVrDriverRegistrationReceipt> expectedReceipts) =>
        throw new NotSupportedException(
            "This receipt store does not provide conditional expected-record batch deletion.");
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

    public bool Delete(SteamVrDriverRegistrationReceipt expectedReceipt) => true;

    public int DeleteAll(IReadOnlyList<SteamVrDriverRegistrationReceipt> expectedReceipts)
    {
        ArgumentNullException.ThrowIfNull(expectedReceipts);
        return expectedReceipts.Count;
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
