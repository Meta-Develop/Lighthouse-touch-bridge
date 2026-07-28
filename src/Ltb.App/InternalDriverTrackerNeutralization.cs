using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ltb.Driver;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.App;

public enum InternalDriverTrackerNeutralizationState
{
    Inactive = 0,
    Recovering,
    Recovered,
    Neutralizing,
    Active,
    Restoring,
    Restored,
    RestoreFailed,
}

/// <summary>Stable evidence for one physical tracker path controlled by LTB.</summary>
public sealed record InternalDriverTrackerPath
{
    public InternalDriverTrackerPath(
        ProtocolHand hand,
        string trackerSerial,
        string devicePath)
    {
        if (hand is not ProtocolHand.Left and not ProtocolHand.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Hand = hand;
        TrackerSerial = trackerSerial;
        DevicePath = devicePath;
    }

    public ProtocolHand Hand { get; }

    public string TrackerSerial { get; }

    public string DevicePath { get; }
}

/// <summary>
/// App-owned lifecycle evidence. The backend snapshot identifier is opaque;
/// native/settings implementations retain the actual reversible snapshot.
/// </summary>
public sealed record InternalDriverTrackerNeutralizationSnapshot(
    InternalDriverTrackerNeutralizationState State,
    IReadOnlyList<InternalDriverTrackerPath> TrackerPaths,
    string? BackendSnapshotId,
    string Diagnostic,
    IReadOnlyList<string> RestoreFailures)
{
    internal static InternalDriverTrackerNeutralizationSnapshot Inactive { get; } = new(
        InternalDriverTrackerNeutralizationState.Inactive,
        Array.Empty<InternalDriverTrackerPath>(),
        BackendSnapshotId: null,
        "Tracker-path neutralization is inactive.",
        Array.Empty<string>());
}

internal sealed record InternalDriverTrackerNeutralizationReceipt(
    string SnapshotId,
    IReadOnlyList<InternalDriverTrackerPath> TrackerPaths)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SnapshotId);
        InternalDriverTrackerNeutralizationLifecycle.ValidateExactPair(TrackerPaths);
    }
}

internal sealed record InternalDriverTrackerRecoveryResult(
    bool Restored,
    string Diagnostic,
    IReadOnlyList<string> Failures)
{
    internal static InternalDriverTrackerRecoveryResult NothingToRecover { get; } = new(
        Restored: true,
        "No retained tracker-path neutralization snapshot required recovery.",
        Array.Empty<string>());
}

/// <summary>
/// Production App boundary for exact tracker-path role neutralization.
/// Implementations capture original state, neutralize the exact supplied paths,
/// and restore only from the receipt.
/// Capture-and-neutralize must be atomic or self-rollback before throwing, and
/// must retain enough durable pending-snapshot state for RecoverAsync to
/// finish rollback after process/session interruption.
/// </summary>
internal interface IInternalDriverTrackerNeutralizationBackend
{
    ValueTask<InternalDriverTrackerNeutralizationReceipt> CaptureAndNeutralizeAsync(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        CancellationToken cancellationToken);

    ValueTask RestoreAsync(
        InternalDriverTrackerNeutralizationReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<InternalDriverTrackerRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InternalDriverTrackerRecoveryResult.NothingToRecover);
}

/// <summary>Optional runtime capability consumed by the App session.</summary>
internal interface IInternalDriverTrackerNeutralizationRuntime
{
    IInternalDriverTrackerNeutralizationBackend TrackerNeutralizationBackend { get; }
}

/// <summary>
/// Production bridge from the App lifecycle to the exact-path
/// <see cref="SteamVrSettingsManager"/> transaction. An intent receipt is
/// persisted before mutation so a later process can distinguish the one
/// transaction-owned backup from unrelated older backups.
/// </summary>
internal sealed class SteamVrSettingsTrackerNeutralizationBackend :
    IInternalDriverTrackerNeutralizationBackend,
    IDisposable
{
    private const int ReceiptSchemaVersion = 1;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISteamVrDriverLifecycle _driverLifecycle;
    private readonly string _receiptPath;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ActiveTransaction? _active;
    private bool _disposed;

    public SteamVrSettingsTrackerNeutralizationBackend(
        ISteamVrDriverLifecycle driverLifecycle,
        string receiptPath)
    {
        _driverLifecycle = driverLifecycle
            ?? throw new ArgumentNullException(nameof(driverLifecycle));
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        _receiptPath = Path.GetFullPath(receiptPath);
    }

    public async ValueTask<InternalDriverTrackerNeutralizationReceipt>
        CaptureAndNeutralizeAsync(
            IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
            CancellationToken cancellationToken)
    {
        InternalDriverTrackerNeutralizationLifecycle.ValidateExactPair(trackerPaths);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null || File.Exists(_receiptPath))
            {
                throw new InvalidOperationException(
                    "A tracker-role recovery receipt already exists; recover it before neutralizing.");
            }

            var paths = await _driverLifecycle.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            var manager = new SteamVrSettingsManager(paths.SettingsFile);
            var priorBackups = manager.FindRecoveryBackups().ToArray();
            var originalHash = HashFile(paths.SettingsFile);
            var immutableTrackerPaths = trackerPaths.ToArray();
            var left = immutableTrackerPaths.Single(path =>
                path.Hand == ProtocolHand.Left);
            var right = immutableTrackerPaths.Single(path =>
                path.Hand == ProtocolHand.Right);
            var expectedPostImageHash = ComputeNeutralizedPostImageHash(
                paths.SettingsFile,
                left.DevicePath,
                right.DevicePath);
            var snapshotId = Guid.NewGuid().ToString("N");
            var durable = new DurableReceipt(
                ReceiptSchemaVersion,
                DurableReceiptPhase.Neutralizing,
                snapshotId,
                paths.SettingsFile,
                originalHash,
                expectedPostImageHash,
                priorBackups,
                BackupFilePath: null,
                left.DevicePath,
                right.DevicePath);
            WriteReceipt(durable);

            SteamVrSettingsRecoveryPoint recoveryPoint;
            try
            {
                recoveryPoint = manager.NeutralizePhysicalTrackerRoles(
                    new PhysicalTrackerRoleTargets(left.DevicePath, right.DevicePath));
                if (recoveryPoint.SettingsChanged)
                {
                    var backupFile = recoveryPoint.BackupFilePath
                        ?? throw new InvalidDataException(
                            "A changed tracker-role transaction returned no recovery backup.");
                    durable = durable with
                    {
                        Phase = DurableReceiptPhase.Active,
                        BackupFilePath = backupFile,
                    };
                    WriteReceipt(durable);
                }
                else
                {
                    DeleteReceipt();
                }
            }
            catch
            {
                // If the settings manager completed a write, the pre-mutation
                // receipt and FindRecoveryBackups evidence remain for the next
                // startup. Never guess a rollback target here.
                throw;
            }

            var receipt = new InternalDriverTrackerNeutralizationReceipt(
                snapshotId,
                Array.AsReadOnly(immutableTrackerPaths));
            _active = new ActiveTransaction(
                manager,
                recoveryPoint,
                receipt,
                recoveryPoint.SettingsChanged);
            return receipt;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask RestoreAsync(
        InternalDriverTrackerNeutralizationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var active = _active
                ?? throw new InvalidOperationException(
                    "No in-process tracker-role transaction is available to restore.");
            if (!string.Equals(
                    active.Receipt.SnapshotId,
                    receipt.SnapshotId,
                    StringComparison.Ordinal) ||
                !active.Receipt.TrackerPaths.SequenceEqual(receipt.TrackerPaths))
            {
                throw new InvalidDataException(
                    "The tracker-role restore receipt does not match the active transaction.");
            }

            _ = active.Manager.RestorePhysicalTrackerRoles(active.RecoveryPoint);
            if (active.HasDurableReceipt)
            {
                DeleteReceipt();
            }

            _active = null;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<InternalDriverTrackerRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(_receiptPath))
            {
                return InternalDriverTrackerRecoveryResult.NothingToRecover;
            }

            DurableReceipt receipt;
            try
            {
                receipt = ReadReceipt();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Failure(
                    $"Tracker-role recovery receipt is invalid: {exception.Message}");
            }

            SteamVrPaths discovered;
            try
            {
                discovered = await _driverLifecycle.DiscoverAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Failure(
                    $"SteamVR settings discovery failed during tracker-role recovery: " +
                    exception.Message);
            }

            if (!PathsEqual(discovered.SettingsFile, receipt.SettingsFilePath))
            {
                return Failure(
                    "The retained tracker-role receipt belongs to a different SteamVR settings file.");
            }

            var manager = new SteamVrSettingsManager(discovered.SettingsFile);
            var discoveredBackups = manager.FindRecoveryBackups();
            var currentHash = HashFile(discovered.SettingsFile);
            if (HashEquals(currentHash, receipt.OriginalSettingsSha256))
            {
                DeleteReceipt();
                return new InternalDriverTrackerRecoveryResult(
                    Restored: true,
                    "The retained tracker-role receipt was cleared because settings already match the captured original.",
                    Array.Empty<string>());
            }

            if (!HashEquals(currentHash, receipt.ExpectedPostImageSha256))
            {
                return Failure(
                    "SteamVR settings no longer match the exact transaction-owned " +
                    "neutralized post-image; automatic recovery was refused.");
            }

            var candidates = receipt.BackupFilePath is { } explicitBackup
                ? discoveredBackups
                    .Where(path => PathsEqual(path, explicitBackup))
                    .Where(path => HashEquals(
                        HashFile(path),
                        receipt.OriginalSettingsSha256))
                    .ToArray()
                : discoveredBackups
                    .Where(path => !receipt.PriorBackupFilePaths.Any(
                        prior => PathsEqual(path, prior)))
                    .Where(path => HashEquals(HashFile(path), receipt.OriginalSettingsSha256))
                    .ToArray();

            if (candidates.Length != 1)
            {
                return Failure(
                    $"Tracker-role recovery found {candidates.Length} unambiguous matching " +
                    "backups; automatic restore was refused.");
            }

            try
            {
                _ = manager.RecoverFromBackup(candidates[0]);
                DeleteReceipt();
                _active = null;
                return new InternalDriverTrackerRecoveryResult(
                    Restored: true,
                    "Recovered the exact pre-neutralization SteamVR settings backup.",
                    Array.Empty<string>());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Failure(
                    $"Tracker-role backup recovery failed: {exception.Message}");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Reads current roles only for the exact settings path and tracker paths
    /// retained by a valid durable LTB receipt. No recovery, neutralization,
    /// restore, receipt deletion, or settings write is performed.
    /// </summary>
    internal static TrackerRoleDrift? InspectRetainedRoleDrift(
        string receiptPath,
        string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        var canonicalReceiptPath = Path.GetFullPath(receiptPath);
        if (!File.Exists(canonicalReceiptPath))
        {
            return null;
        }

        var receipt = ReadReceipt(canonicalReceiptPath);
        if (!PathsEqual(receipt.SettingsFilePath, settingsFilePath))
        {
            throw new InvalidDataException(
                "The retained tracker-role receipt belongs to a different SteamVR settings file.");
        }

        return new SteamVrSettingsManager(settingsFilePath)
            .InspectPhysicalTrackerRoleDrift(
                new PhysicalTrackerRoleTargets(
                    receipt.LeftTrackerDevicePath,
                    receipt.RightTrackerDevicePath));
    }

    private DurableReceipt ReadReceipt()
        => ReadReceipt(_receiptPath);

    private static DurableReceipt ReadReceipt(string receiptPath)
    {
        var receipt = JsonSerializer.Deserialize<DurableReceipt>(
            File.ReadAllText(receiptPath, Encoding.UTF8),
            ReceiptJsonOptions)
            ?? throw new InvalidDataException("Tracker-role recovery receipt is null.");
        if (receipt.SchemaVersion != ReceiptSchemaVersion ||
            !Enum.IsDefined(receipt.Phase) ||
            string.IsNullOrWhiteSpace(receipt.SnapshotId) ||
            !Guid.TryParseExact(receipt.SnapshotId, "N", out _) ||
            string.IsNullOrWhiteSpace(receipt.SettingsFilePath) ||
            !Path.IsPathFullyQualified(receipt.SettingsFilePath) ||
            receipt.OriginalSettingsSha256.Length != 64 ||
            receipt.ExpectedPostImageSha256.Length != 64 ||
            receipt.PriorBackupFilePaths is null ||
            string.IsNullOrWhiteSpace(receipt.LeftTrackerDevicePath) ||
            string.IsNullOrWhiteSpace(receipt.RightTrackerDevicePath))
        {
            throw new InvalidDataException(
                "Tracker-role recovery receipt failed schema validation.");
        }

        _ = new PhysicalTrackerRoleTargets(
            receipt.LeftTrackerDevicePath,
            receipt.RightTrackerDevicePath);
        return receipt;
    }

    private void WriteReceipt(DurableReceipt receipt)
    {
        var directory = Path.GetDirectoryName(_receiptPath)
            ?? throw new InvalidOperationException(
                "Tracker-role recovery receipt has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(_receiptPath)}.{Guid.NewGuid():N}.write");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptJsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _receiptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void DeleteReceipt()
    {
        if (File.Exists(_receiptPath))
        {
            File.Delete(_receiptPath);
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ComputeNeutralizedPostImageHash(
        string settingsFilePath,
        string leftTrackerDevicePath,
        string rightTrackerDevicePath)
    {
        var root = JsonNode.Parse(
            File.ReadAllBytes(settingsFilePath),
            nodeOptions: null,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject
            ?? throw new InvalidDataException(
                "steamvr.vrsettings root must be a JSON object.");
        JsonObject trackers;
        if (root.TryGetPropertyValue("trackers", out var existing))
        {
            trackers = existing as JsonObject
                ?? throw new InvalidDataException(
                    "SteamVR settings property 'trackers' must be a JSON object.");
        }
        else
        {
            trackers = new JsonObject();
            root["trackers"] = trackers;
        }

        trackers[leftTrackerDevicePath] = "TrackerRole_None";
        trackers[rightTrackerDevicePath] = "TrackerRole_None";
        var bytes = Encoding.UTF8.GetBytes(
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InternalDriverTrackerRecoveryResult Failure(string diagnostic) =>
        new(
            Restored: false,
            diagnostic,
            [diagnostic]);

    private enum DurableReceiptPhase
    {
        Neutralizing = 0,
        Active,
    }

    private sealed record DurableReceipt(
        int SchemaVersion,
        DurableReceiptPhase Phase,
        string SnapshotId,
        string SettingsFilePath,
        string OriginalSettingsSha256,
        string ExpectedPostImageSha256,
        IReadOnlyList<string> PriorBackupFilePaths,
        string? BackupFilePath,
        string LeftTrackerDevicePath,
        string RightTrackerDevicePath);

    private sealed record ActiveTransaction(
        SteamVrSettingsManager Manager,
        SteamVrSettingsRecoveryPoint RecoveryPoint,
        InternalDriverTrackerNeutralizationReceipt Receipt,
        bool HasDurableReceipt);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationGate.Dispose();
    }
}

/// <summary>
/// Linear App lifecycle around a backend-owned reversible snapshot. It accepts
/// exactly one left and one right physical tracker path and retains failures
/// for structured session diagnostics.
/// </summary>
internal sealed class InternalDriverTrackerNeutralizationLifecycle
{
    private readonly IInternalDriverTrackerNeutralizationBackend _backend;
    private readonly Action<InternalDriverTrackerNeutralizationSnapshot>? _onChanged;
    private InternalDriverTrackerNeutralizationReceipt? _receipt;
    private InternalDriverTrackerNeutralizationSnapshot _snapshot =
        InternalDriverTrackerNeutralizationSnapshot.Inactive;

    public InternalDriverTrackerNeutralizationLifecycle(
        IInternalDriverTrackerNeutralizationBackend backend,
        Action<InternalDriverTrackerNeutralizationSnapshot>? onChanged = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _onChanged = onChanged;
    }

    public InternalDriverTrackerNeutralizationSnapshot Snapshot =>
        Volatile.Read(ref _snapshot);

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        Publish(
            InternalDriverTrackerNeutralizationState.Recovering,
            Array.Empty<InternalDriverTrackerPath>(),
            snapshotId: null,
            "Recovering any retained tracker-path snapshot before a new session.",
            Array.Empty<string>());
        InternalDriverTrackerRecoveryResult recovered;
        try
        {
            recovered = await _backend.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                Array.Empty<InternalDriverTrackerPath>(),
                snapshotId: null,
                $"Retained tracker-path recovery failed: {exception.Message}",
                [exception.Message]);
            throw new InvalidOperationException(
                "Retained tracker-path recovery failed before a new session could start.",
                exception);
        }

        ArgumentNullException.ThrowIfNull(recovered);
        Publish(
            recovered.Restored
                ? InternalDriverTrackerNeutralizationState.Recovered
                : InternalDriverTrackerNeutralizationState.RestoreFailed,
            Array.Empty<InternalDriverTrackerPath>(),
            snapshotId: null,
            recovered.Diagnostic,
            recovered.Failures);
        if (!recovered.Restored)
        {
            throw new InvalidOperationException(
                $"Retained tracker-path recovery failed: {recovered.Diagnostic}");
        }

        _receipt = null;
    }

    public async ValueTask ActivateAsync(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        CancellationToken cancellationToken)
    {
        ValidateExactPair(trackerPaths);
        if (_receipt is not null)
        {
            throw new InvalidOperationException(
                "Tracker paths are already neutralized for this App lifecycle.");
        }

        var immutablePaths = Array.AsReadOnly(trackerPaths.ToArray());
        Publish(
            InternalDriverTrackerNeutralizationState.Neutralizing,
            immutablePaths,
            snapshotId: null,
            "Capturing and neutralizing exactly two controller-source tracker paths.",
            Array.Empty<string>());
        InternalDriverTrackerNeutralizationReceipt receipt;
        try
        {
            var returnedReceipt = await _backend
                .CaptureAndNeutralizeAsync(immutablePaths, cancellationToken)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(returnedReceipt);
            returnedReceipt.Validate();
            if (!returnedReceipt.TrackerPaths.SequenceEqual(immutablePaths))
            {
                throw new InvalidDataException(
                    "The tracker neutralization backend receipt does not match the exact requested paths.");
            }

            // Never retain a backend/caller-owned mutable IReadOnlyList.
            receipt = new InternalDriverTrackerNeutralizationReceipt(
                returnedReceipt.SnapshotId,
                immutablePaths);
        }
        catch (Exception activationFailure) when (activationFailure is not OutOfMemoryException)
        {
            InternalDriverTrackerRecoveryResult recovery;
            try
            {
                recovery = await _backend.RecoverAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception recoveryFailure) when (recoveryFailure is not OutOfMemoryException)
            {
                Publish(
                    InternalDriverTrackerNeutralizationState.RestoreFailed,
                    immutablePaths,
                    snapshotId: null,
                    $"Tracker-path activation failed ({activationFailure.Message}) and durable " +
                    $"recovery also failed ({recoveryFailure.Message}).",
                    [activationFailure.Message, recoveryFailure.Message]);
                throw new AggregateException(
                    "Tracker-path activation and durable recovery both failed.",
                    activationFailure,
                    recoveryFailure);
            }

            Publish(
                recovery.Restored
                    ? InternalDriverTrackerNeutralizationState.Restored
                    : InternalDriverTrackerNeutralizationState.RestoreFailed,
                immutablePaths,
                snapshotId: null,
                $"Tracker-path activation failed ({activationFailure.Message}); " +
                recovery.Diagnostic,
                recovery.Failures.Count == 0
                    ? [activationFailure.Message]
                    : [activationFailure.Message, .. recovery.Failures]);
            if (!recovery.Restored)
            {
                throw new AggregateException(
                    "Tracker-path activation failed and durable recovery was incomplete.",
                    [activationFailure, new InvalidOperationException(recovery.Diagnostic)]);
            }

            throw;
        }

        _receipt = receipt;
        Publish(
            InternalDriverTrackerNeutralizationState.Active,
            immutablePaths,
            receipt.SnapshotId,
            "Exactly two controller-source tracker paths are neutralized while LTB is Active.",
            Array.Empty<string>());
    }

    public async ValueTask<bool> RestoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var receipt = _receipt;
        if (receipt is null)
        {
            return true;
        }

        Publish(
            InternalDriverTrackerNeutralizationState.Restoring,
            receipt.TrackerPaths,
            receipt.SnapshotId,
            $"Restoring the exact two tracker paths after {reason}.",
            Array.Empty<string>());
        try
        {
            await _backend.RestoreAsync(receipt, cancellationToken).ConfigureAwait(false);
            _receipt = null;
            Publish(
                InternalDriverTrackerNeutralizationState.Restored,
                receipt.TrackerPaths,
                receipt.SnapshotId,
                $"Restored the exact two tracker paths after {reason}.",
                Array.Empty<string>());
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                receipt.TrackerPaths,
                receipt.SnapshotId,
                $"Tracker-path restore failed after {reason}: {exception.Message}",
                [exception.Message]);
            return false;
        }
    }

    internal static void ValidateExactPair(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths)
    {
        ArgumentNullException.ThrowIfNull(trackerPaths);
        if (trackerPaths.Count != 2 ||
            trackerPaths.Count(path => path.Hand == ProtocolHand.Left) != 1 ||
            trackerPaths.Count(path => path.Hand == ProtocolHand.Right) != 1 ||
            trackerPaths.Select(path => path.TrackerSerial)
                .Distinct(StringComparer.Ordinal).Count() != 2 ||
            trackerPaths.Select(path => path.DevicePath)
                .Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new ArgumentException(
                "Tracker neutralization requires exactly one distinct left path and one distinct right path.",
                nameof(trackerPaths));
        }
    }

    private void Publish(
        InternalDriverTrackerNeutralizationState state,
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        string? snapshotId,
        string diagnostic,
        IReadOnlyList<string> failures)
    {
        var snapshot = new InternalDriverTrackerNeutralizationSnapshot(
            state,
            Array.AsReadOnly(trackerPaths.ToArray()),
            snapshotId,
            diagnostic,
            Array.AsReadOnly(failures.ToArray()));
        Volatile.Write(
            ref _snapshot,
            snapshot);
        _onChanged?.Invoke(snapshot);
    }
}
