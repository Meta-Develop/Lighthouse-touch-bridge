using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ltb.OpenVr;

/// <summary>
/// Performs fail-safe, file-level updates to one explicitly selected
/// <c>steamvr.vrsettings</c> file. This type never searches for a settings file.
/// </summary>
public sealed class SteamVrSettingsManager
{
    private const string SteamVrSectionName = "steamvr";
    private const string ActivateMultipleDriversName = "activateMultipleDrivers";
    private const string TrackingOverridesSectionName = "TrackingOverrides";
    private const string TrackersSectionName = "trackers";
    private const string NeutralTrackerRole = "TrackerRole_None";
    private const string BackupMarker = ".ltb-backup";
    private const string BackupWriteMarker = ".ltb-backup-write";
    private const string TemporaryMarker = ".ltb-write";
    private const string LockMarker = ".ltb-lock";
    private const int MaximumUniqueSiblingAttempts = 1024;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(20);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Action<string>? _afterAtomicWrite;
    private readonly Action<string>? _beforeFinalChangeCheck;
    private readonly TimeSpan _lockTimeout;

    public SteamVrSettingsManager(string settingsFilePath)
        : this(
            settingsFilePath,
            afterAtomicWrite: null,
            beforeFinalChangeCheck: null,
            lockTimeout: null)
    {
    }

    internal SteamVrSettingsManager(
        string settingsFilePath,
        Action<string>? afterAtomicWrite,
        Action<string>? beforeFinalChangeCheck = null,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        SettingsFilePath = Path.GetFullPath(settingsFilePath);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(SettingsFilePath)))
        {
            throw new ArgumentException(
                "A SteamVR settings file path is required.",
                nameof(settingsFilePath));
        }

        _afterAtomicWrite = afterAtomicWrite;
        _beforeFinalChangeCheck = beforeFinalChangeCheck;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        if (_lockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lockTimeout),
                "Lock timeout must not be negative.");
        }
    }

    public string SettingsFilePath { get; }

    /// <summary>
    /// Enables exactly one discovered VMT-to-Touch mapping and ensures that
    /// SteamVR permits multiple drivers. Conflicting source or hand mappings
    /// fail closed instead of being replaced.
    /// </summary>
    public SteamVrSettingsRecoveryPoint EnableOverride(
        TrackingOverrideBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.EnableTrackingOverride,
            binding,
            root => EnableOverride(root, binding),
            root => ValidateEnabled(root, binding));
    }

    /// <summary>
    /// Removes only the requested mapping. It deliberately preserves
    /// <c>activateMultipleDrivers</c> and every other override and setting.
    /// </summary>
    public SteamVrSettingsRecoveryPoint ReleaseOverride(
        TrackingOverrideBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.ReleaseTrackingOverride,
            binding,
            root => ReleaseOverride(root, binding),
            root => ValidateReleased(root, binding));
    }

    /// <summary>
    /// Removes every pose-source mapping that targets one semantic Touch hand.
    /// This is the calibration safety operation: the source path may belong to
    /// another tool or an earlier VMT slot, so matching by a caller-assumed
    /// source path is not sufficient. Unrelated mappings and settings are
    /// preserved, including malformed values that do not represent a hand
    /// target.
    /// </summary>
    public SteamVrSettingsRecoveryPoint ReleaseOverridesTargetingSemanticHand(
        string semanticHandPath)
    {
        ValidateSemanticHandPath(semanticHandPath);
        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.ReleaseSemanticHandOverrides,
            binding: null,
            root => ReleaseOverridesTargetingSemanticHand(root, semanticHandPath),
            root => ValidateSemanticHandReleased(root, semanticHandPath));
    }

    /// <summary>
    /// Atomically removes every mapping that either references the configured
    /// application pose source or targets its intended semantic hand. Additional
    /// discovered pose-source paths may be included when the active runtime path
    /// differs from the configured path. Unrelated settings and mappings are
    /// preserved, and no unknown source mapping is recreated.
    /// </summary>
    public SteamVrSettingsRecoveryPoint ReleaseApplicationSafetyOverrides(
        TrackingOverrideBinding configuredBinding,
        params string[] additionalPoseSourceDevicePaths)
    {
        ArgumentNullException.ThrowIfNull(configuredBinding);
        ArgumentNullException.ThrowIfNull(additionalPoseSourceDevicePaths);
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal)
        {
            configuredBinding.PoseSourceDevicePath,
        };
        foreach (var sourcePath in additionalPoseSourceDevicePaths)
        {
            _ = new TrackingOverrideBinding(
                sourcePath,
                configuredBinding.SemanticHandPath);
            _ = sourcePaths.Add(sourcePath);
        }

        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.ReleaseApplicationSafetyOverrides,
            configuredBinding,
            root => ReleaseApplicationSafetyOverrides(
                root,
                sourcePaths,
                configuredBinding.SemanticHandPath),
            root => ValidateApplicationSafetyOverridesReleased(
                root,
                sourcePaths,
                configuredBinding.SemanticHandPath));
    }

    /// <summary>
    /// Sets exactly two caller-supplied physical tracker registered-device
    /// paths to SteamVR's neutral tracker role. The exact prior presence and
    /// JSON value of each target are captured for a later targeted restore.
    /// Every unrelated setting is preserved.
    /// </summary>
    public SteamVrSettingsRecoveryPoint NeutralizePhysicalTrackerRoles(
        PhysicalTrackerRoleTargets targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.NeutralizePhysicalTrackerRoles,
            binding: null,
            root => NeutralizePhysicalTrackerRoles(root, targets),
            root => ValidatePhysicalTrackerRolesNeutralized(root, targets),
            root => CapturePhysicalTrackerRoleState(root, targets));
    }

    /// <summary>
    /// Restores only the two physical tracker role entries captured by
    /// <see cref="NeutralizePhysicalTrackerRoles"/>. Each target must still be
    /// neutral or already equal to its own prior value; an unrelated later
    /// settings change is preserved.
    /// </summary>
    public SteamVrSettingsRecoveryPoint RestorePhysicalTrackerRoles(
        SteamVrSettingsRecoveryPoint recoveryPoint)
    {
        var priorState = RequirePhysicalTrackerRoleState(recoveryPoint);

        using var operationLock = AcquireOperationLock();
        return ApplyJsonMutation(
            SteamVrSettingsOperation.RestorePhysicalTrackerRoles,
            binding: null,
            root => RestorePhysicalTrackerRoles(root, priorState),
            root => ValidatePhysicalTrackerRolesRestored(root, priorState));
    }

    /// <summary>
    /// Reads the current roles for the exact two paths captured by a completed
    /// physical-tracker neutralization and reports whether either path drifted.
    /// This inspection does not acquire the settings-operation lock, create a
    /// backup, restore, neutralize, or otherwise write any file.
    /// </summary>
    public TrackerRoleDrift InspectPhysicalTrackerRoleDrift(
        SteamVrSettingsRecoveryPoint neutralizationRecoveryPoint)
    {
        var priorState = RequirePhysicalTrackerRoleState(
            neutralizationRecoveryPoint);
        var root = ParseRoot(ReadSettingsBytes(), SettingsFilePath);
        var trackersSectionIsPresent = root.TryGetPropertyValue(
            TrackersSectionName,
            out var trackersNode);
        var trackers = trackersNode as JsonObject;
        var invalidTrackersSection =
            trackersSectionIsPresent && trackers is null;

        return new TrackerRoleDrift(
            priorState.Targets,
            InspectPhysicalTrackerRole(
                trackers,
                invalidTrackersSection,
                priorState.Targets.LeftTrackerDevicePath),
            InspectPhysicalTrackerRole(
                trackers,
                invalidTrackersSection,
                priorState.Targets.RightTrackerDevicePath));
    }

    /// <summary>
    /// Restores the bytes captured by a completed operation. The content being
    /// replaced is backed up first, so the returned recovery point can undo
    /// this rollback if necessary.
    /// </summary>
    public SteamVrSettingsRecoveryPoint Rollback(
        SteamVrSettingsRecoveryPoint recoveryPoint)
    {
        ArgumentNullException.ThrowIfNull(recoveryPoint);
        if (!PathsEqual(recoveryPoint.SettingsFilePath, SettingsFilePath))
        {
            throw new ArgumentException(
                "The recovery point belongs to a different SteamVR settings file.",
                nameof(recoveryPoint));
        }

        if (recoveryPoint.BackupFilePath is null)
        {
            return new SteamVrSettingsRecoveryPoint(
                SettingsFilePath,
                backupFilePath: null,
                SteamVrSettingsOperation.RestoreBackup,
                binding: null,
                settingsChanged: false,
                expectedPostImage: null);
        }

        using var operationLock = AcquireOperationLock();
        var expectedPostImage = recoveryPoint.ExpectedPostImage
            ?? throw new InvalidOperationException(
                "The recovery point has no owned post-image and cannot be rolled back safely.");
        if (!ReadSettingsBytes().AsSpan().SequenceEqual(expectedPostImage))
        {
            throw new IOException(
                "steamvr.vrsettings changed after this recovery point; explicit rollback " +
                "was refused so the later writer remains intact.");
        }

        return RecoverFromBackupCore(
            recoveryPoint.BackupFilePath,
            requireValidBackupJson: false);
    }

    /// <summary>
    /// Restores a manager-created sibling backup after an interrupted process.
    /// Arbitrary files and backups belonging to another settings path are
    /// rejected.
    /// </summary>
    public SteamVrSettingsRecoveryPoint RecoverFromBackup(string backupFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        using var operationLock = AcquireOperationLock();
        return RecoverFromBackupCore(backupFilePath, requireValidBackupJson: true);
    }

    private SteamVrSettingsRecoveryPoint RecoverFromBackupCore(
        string backupFilePath,
        bool requireValidBackupJson)
    {
        var fullBackupPath = ValidateBackupPath(backupFilePath);
        var restoredBytes = File.ReadAllBytes(fullBackupPath);
        if (requireValidBackupJson)
        {
            _ = ParseRoot(restoredBytes, fullBackupPath);
        }

        var currentBytes = ReadSettingsBytes();
        if (currentBytes.AsSpan().SequenceEqual(restoredBytes))
        {
            return new SteamVrSettingsRecoveryPoint(
                SettingsFilePath,
                backupFilePath: null,
                SteamVrSettingsOperation.RestoreBackup,
                binding: null,
                settingsChanged: false,
                expectedPostImage: null);
        }

        var safetyBackupPath = CreateUniqueBackup(currentBytes);
        _beforeFinalChangeCheck?.Invoke(SettingsFilePath);
        EnsureSettingsUnchanged(currentBytes);
        var targetReplaced = false;
        try
        {
            WriteAtomically(restoredBytes);
            targetReplaced = true;
            ValidateExactBytes(restoredBytes, requireValidBackupJson);
            return new SteamVrSettingsRecoveryPoint(
                SettingsFilePath,
                safetyBackupPath,
                SteamVrSettingsOperation.RestoreBackup,
                binding: null,
                settingsChanged: true,
                expectedPostImage: restoredBytes);
        }
        catch (Exception failure) when (targetReplaced)
        {
            ThrowAfterAutomaticRestore(
                currentBytes,
                restoredBytes,
                safetyBackupPath,
                failure);
            throw;
        }
    }

    /// <summary>
    /// Discovers recognized regular-file sibling backups in ordinal name
    /// order. Discovery reads only filesystem metadata and never backup
    /// contents.
    /// </summary>
    public SteamVrSettingsRecoveryDiscovery DiscoverRecoveryBackups()
    {
        var directory = GetSettingsDirectory();
        if (!Directory.Exists(directory))
        {
            return new SteamVrSettingsRecoveryDiscovery(
                SettingsFilePath,
                Array.Empty<SteamVrSettingsRecoveryCandidate>());
        }

        var backupPrefix = Path.GetFileName(SettingsFilePath) + BackupMarker;
        var candidates = new List<SteamVrSettingsRecoveryCandidate>();
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory,
                     backupPrefix + "*"))
        {
            var fileName = Path.GetFileName(path);
            if (!TryParseRecognizedBackupName(
                    fileName,
                    backupPrefix,
                    out var sequenceNumber) ||
                !TryGetSafeRecoveryCandidate(
                    path,
                    fileName,
                    sequenceNumber,
                    out var candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return new SteamVrSettingsRecoveryDiscovery(
            SettingsFilePath,
            candidates.OrderBy(
                candidate => candidate.BackupFilePath,
                StringComparer.Ordinal));
    }

    /// <summary>Lists manager-created sibling backups in ordinal name order.</summary>
    public IReadOnlyList<string> FindRecoveryBackups()
        => DiscoverRecoveryBackups()
            .Candidates
            .Select(candidate => candidate.BackupFilePath)
            .ToArray();

    private SteamVrSettingsRecoveryPoint ApplyJsonMutation(
        SteamVrSettingsOperation operation,
        TrackingOverrideBinding? binding,
        Func<JsonObject, bool> mutate,
        Action<JsonObject> validateOperation,
        Func<JsonObject, PhysicalTrackerRoleState?>? capturePhysicalTrackerRoleState = null)
    {
        var originalBytes = ReadSettingsBytes();
        var root = ParseRoot(originalBytes, SettingsFilePath);
        var physicalTrackerRoleState = capturePhysicalTrackerRoleState?.Invoke(root);
        if (!mutate(root))
        {
            EnsureSettingsUnchanged(originalBytes);
            validateOperation(root);
            return new SteamVrSettingsRecoveryPoint(
                SettingsFilePath,
                backupFilePath: null,
                operation,
                binding,
                settingsChanged: false,
                expectedPostImage: null,
                physicalTrackerRoleState);
        }

        var updatedBytes = Serialize(root);
        EnsureSettingsUnchanged(originalBytes);
        var backupFilePath = CreateUniqueBackup(originalBytes);
        _beforeFinalChangeCheck?.Invoke(SettingsFilePath);
        EnsureSettingsUnchanged(originalBytes);

        var targetReplaced = false;
        try
        {
            WriteAtomically(updatedBytes);
            targetReplaced = true;
            _afterAtomicWrite?.Invoke(SettingsFilePath);

            var writtenRoot = ParseRoot(ReadSettingsBytes(), SettingsFilePath);
            if (!JsonNode.DeepEquals(root, writtenRoot))
            {
                throw new InvalidDataException(
                    "Post-write validation found settings different from the intended merge.");
            }

            validateOperation(writtenRoot);
            return new SteamVrSettingsRecoveryPoint(
                SettingsFilePath,
                backupFilePath,
                operation,
                binding,
                settingsChanged: true,
                expectedPostImage: updatedBytes,
                physicalTrackerRoleState);
        }
        catch (Exception failure) when (targetReplaced)
        {
            ThrowAfterAutomaticRestore(
                originalBytes,
                updatedBytes,
                backupFilePath,
                failure);
            throw;
        }
    }

    private static PhysicalTrackerRoleState CapturePhysicalTrackerRoleState(
        JsonObject root,
        PhysicalTrackerRoleTargets targets)
    {
        var sectionWasPresent = root.TryGetPropertyValue(
            TrackersSectionName,
            out var trackersNode);
        JsonObject? trackers = null;
        if (sectionWasPresent)
        {
            trackers = trackersNode as JsonObject
                ?? throw WrongSectionType(TrackersSectionName);
        }

        return new PhysicalTrackerRoleState(
            targets,
            sectionWasPresent,
            CapturePhysicalTrackerRoleSnapshot(
                trackers,
                targets.LeftTrackerDevicePath),
            CapturePhysicalTrackerRoleSnapshot(
                trackers,
                targets.RightTrackerDevicePath));
    }

    private PhysicalTrackerRoleState RequirePhysicalTrackerRoleState(
        SteamVrSettingsRecoveryPoint recoveryPoint)
    {
        ArgumentNullException.ThrowIfNull(recoveryPoint);
        if (!PathsEqual(recoveryPoint.SettingsFilePath, SettingsFilePath))
        {
            throw new ArgumentException(
                "The recovery point belongs to a different SteamVR settings file.",
                nameof(recoveryPoint));
        }

        if (recoveryPoint.Operation is not
                SteamVrSettingsOperation.NeutralizePhysicalTrackerRoles ||
            recoveryPoint.PhysicalTrackerRoleState is not { } priorState)
        {
            throw new ArgumentException(
                "The recovery point was not created by a physical tracker role " +
                "neutralization operation.",
                nameof(recoveryPoint));
        }

        return priorState;
    }

    private static TrackerRoleDriftEntry InspectPhysicalTrackerRole(
        JsonObject? trackers,
        bool invalidTrackersSection,
        string registeredDevicePath)
    {
        if (invalidTrackersSection)
        {
            return new TrackerRoleDriftEntry(
                registeredDevicePath,
                TrackerRoleDriftStatus.Changed,
                observedRole: null);
        }

        if (trackers is null ||
            !trackers.TryGetPropertyValue(
                registeredDevicePath,
                out var currentValue))
        {
            return new TrackerRoleDriftEntry(
                registeredDevicePath,
                TrackerRoleDriftStatus.Missing,
                observedRole: null);
        }

        if (IsNeutralPhysicalTrackerRole(currentValue))
        {
            return new TrackerRoleDriftEntry(
                registeredDevicePath,
                TrackerRoleDriftStatus.UnchangedNeutral,
                NeutralTrackerRole);
        }

        var observedRole =
            currentValue is JsonValue value &&
            value.TryGetValue<string>(out var stringRole)
                ? stringRole
                : null;
        return new TrackerRoleDriftEntry(
            registeredDevicePath,
            TrackerRoleDriftStatus.Changed,
            observedRole);
    }

    private static PhysicalTrackerRoleSnapshot CapturePhysicalTrackerRoleSnapshot(
        JsonObject? trackers,
        string registeredDevicePath)
    {
        JsonNode? previousValue = null;
        var wasPresent = trackers is not null &&
            trackers.TryGetPropertyValue(registeredDevicePath, out previousValue);
        return new PhysicalTrackerRoleSnapshot(
            registeredDevicePath,
            wasPresent,
            previousValue);
    }

    private static bool NeutralizePhysicalTrackerRoles(
        JsonObject root,
        PhysicalTrackerRoleTargets targets)
    {
        var changed = false;
        var trackers = GetOrCreateObject(root, TrackersSectionName, ref changed);
        changed |= SetNeutralPhysicalTrackerRole(
            trackers,
            targets.LeftTrackerDevicePath);
        changed |= SetNeutralPhysicalTrackerRole(
            trackers,
            targets.RightTrackerDevicePath);
        return changed;
    }

    private static bool SetNeutralPhysicalTrackerRole(
        JsonObject trackers,
        string registeredDevicePath)
    {
        if (trackers.TryGetPropertyValue(
                registeredDevicePath,
                out var existingValue) &&
            IsNeutralPhysicalTrackerRole(existingValue))
        {
            return false;
        }

        trackers[registeredDevicePath] = NeutralTrackerRole;
        return true;
    }

    private static void ValidatePhysicalTrackerRolesNeutralized(
        JsonObject root,
        PhysicalTrackerRoleTargets targets)
    {
        var trackers = RequireObject(root, TrackersSectionName);
        ValidatePhysicalTrackerRoleNeutralized(
            trackers,
            targets.LeftTrackerDevicePath);
        ValidatePhysicalTrackerRoleNeutralized(
            trackers,
            targets.RightTrackerDevicePath);
    }

    private static void ValidatePhysicalTrackerRoleNeutralized(
        JsonObject trackers,
        string registeredDevicePath)
    {
        if (!trackers.TryGetPropertyValue(
                registeredDevicePath,
                out var roleValue) ||
            !IsNeutralPhysicalTrackerRole(roleValue))
        {
            throw new InvalidDataException(
                $"SteamVR tracker role '{registeredDevicePath}' was not neutralized.");
        }
    }

    private static bool RestorePhysicalTrackerRoles(
        JsonObject root,
        PhysicalTrackerRoleState priorState)
    {
        var sectionIsPresent = root.TryGetPropertyValue(
            TrackersSectionName,
            out var trackersNode);
        var trackers = sectionIsPresent
            ? trackersNode as JsonObject
                ?? throw WrongSectionType(TrackersSectionName)
            : new JsonObject();

        foreach (var snapshot in priorState.Snapshots)
        {
            var targetIsPresent = trackers.TryGetPropertyValue(
                snapshot.RegisteredDevicePath,
                out var currentValue);
            if ((!targetIsPresent ||
                    !IsNeutralPhysicalTrackerRole(currentValue)) &&
                !snapshot.Matches(trackers))
            {
                throw new InvalidOperationException(
                    $"SteamVR tracker role '{snapshot.RegisteredDevicePath}' changed " +
                    "after LTB neutralized it. Restore was refused because its current " +
                    "value is neither the neutral role written by LTB nor its captured " +
                    "prior state.");
            }
        }

        var changed = false;
        foreach (var snapshot in priorState.Snapshots)
        {
            if (snapshot.Matches(trackers))
            {
                continue;
            }

            if (snapshot.WasPresent)
            {
                trackers[snapshot.RegisteredDevicePath] =
                    snapshot.ClonePreviousValue();
            }
            else
            {
                _ = trackers.Remove(snapshot.RegisteredDevicePath);
            }

            changed = true;
        }

        if (priorState.TrackersSectionWasPresent)
        {
            if (!sectionIsPresent)
            {
                root.Add(TrackersSectionName, trackers);
                changed = true;
            }
        }
        else if (sectionIsPresent && trackers.Count == 0)
        {
            _ = root.Remove(TrackersSectionName);
            changed = true;
        }

        return changed;
    }

    private static void ValidatePhysicalTrackerRolesRestored(
        JsonObject root,
        PhysicalTrackerRoleState priorState)
    {
        var sectionIsPresent = root.TryGetPropertyValue(
            TrackersSectionName,
            out var trackersNode);
        if (sectionIsPresent && trackersNode is not JsonObject)
        {
            throw WrongSectionType(TrackersSectionName);
        }

        if (priorState.TrackersSectionWasPresent && !sectionIsPresent)
        {
            throw new InvalidDataException(
                "The prior SteamVR 'trackers' section presence was not restored.");
        }

        var trackers = trackersNode as JsonObject ?? new JsonObject();
        if (!priorState.TrackersSectionWasPresent &&
            sectionIsPresent &&
            trackers.Count == 0)
        {
            throw new InvalidDataException(
                "The 'trackers' section introduced by LTB was not removed.");
        }

        foreach (var snapshot in priorState.Snapshots)
        {
            if (!snapshot.Matches(trackers))
            {
                throw new InvalidDataException(
                    $"The prior SteamVR tracker role " +
                    $"'{snapshot.RegisteredDevicePath}' was not restored.");
            }
        }
    }

    private static bool IsNeutralPhysicalTrackerRole(JsonNode? roleValue) =>
        roleValue is JsonValue value &&
        value.TryGetValue<string>(out var role) &&
        string.Equals(role, NeutralTrackerRole, StringComparison.Ordinal);

    private static bool EnableOverride(
        JsonObject root,
        TrackingOverrideBinding binding)
    {
        var changed = false;
        var steamVr = GetOrCreateObject(root, SteamVrSectionName, ref changed);
        if (!steamVr.TryGetPropertyValue(ActivateMultipleDriversName, out var activeNode) ||
            activeNode is not JsonValue activeValue ||
            !activeValue.TryGetValue<bool>(out var active) ||
            !active)
        {
            steamVr[ActivateMultipleDriversName] = true;
            changed = true;
        }

        var overrides = GetOrCreateObject(
            root,
            TrackingOverridesSectionName,
            ref changed);
        ValidateOverrideValueTypes(overrides);

        var conflictingSource = overrides.FirstOrDefault(pair =>
            !string.Equals(
                pair.Key,
                binding.PoseSourceDevicePath,
                StringComparison.Ordinal) &&
            string.Equals(
                GetOverrideTarget(pair.Key, pair.Value),
                binding.SemanticHandPath,
                StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(conflictingSource.Key))
        {
            throw new InvalidOperationException(
                $"Semantic hand '{binding.SemanticHandPath}' is already supplied by " +
                $"'{conflictingSource.Key}'.");
        }

        if (overrides.TryGetPropertyValue(binding.PoseSourceDevicePath, out var existing))
        {
            var existingTarget = GetOverrideTarget(
                binding.PoseSourceDevicePath,
                existing);
            if (!string.Equals(
                    existingTarget,
                    binding.SemanticHandPath,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pose source '{binding.PoseSourceDevicePath}' is already mapped to " +
                    $"'{existingTarget}'.");
            }
        }
        else
        {
            overrides.Add(binding.PoseSourceDevicePath, binding.SemanticHandPath);
            changed = true;
        }

        return changed;
    }

    private static bool ReleaseOverride(
        JsonObject root,
        TrackingOverrideBinding binding)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return false;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        if (!overrides.TryGetPropertyValue(binding.PoseSourceDevicePath, out var existing))
        {
            return false;
        }

        var existingTarget = GetOverrideTarget(binding.PoseSourceDevicePath, existing);
        if (!string.Equals(
                existingTarget,
                binding.SemanticHandPath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to release pose source '{binding.PoseSourceDevicePath}' because " +
                $"it is mapped to '{existingTarget}', not '{binding.SemanticHandPath}'.");
        }

        return overrides.Remove(binding.PoseSourceDevicePath);
    }

    private static bool ReleaseOverridesTargetingSemanticHand(
        JsonObject root,
        string semanticHandPath)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return false;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        var matchingSources = overrides
            .Where(pair =>
                pair.Value is JsonValue value &&
                value.TryGetValue<string>(out var targetPath) &&
                string.Equals(targetPath, semanticHandPath, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var source in matchingSources)
        {
            _ = overrides.Remove(source);
        }

        return matchingSources.Length > 0;
    }

    private static bool ReleaseApplicationSafetyOverrides(
        JsonObject root,
        IReadOnlySet<string> poseSourceDevicePaths,
        string semanticHandPath)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return false;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        var matchingSources = overrides
            .Where(pair =>
                poseSourceDevicePaths.Contains(pair.Key) ||
                pair.Value is JsonValue value &&
                value.TryGetValue<string>(out var targetPath) &&
                string.Equals(targetPath, semanticHandPath, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var source in matchingSources)
        {
            _ = overrides.Remove(source);
        }

        return matchingSources.Length > 0;
    }

    private static void ValidateEnabled(
        JsonObject root,
        TrackingOverrideBinding binding)
    {
        var steamVr = RequireObject(root, SteamVrSectionName);
        if (!steamVr.TryGetPropertyValue(ActivateMultipleDriversName, out var activeNode) ||
            activeNode is not JsonValue activeValue ||
            !activeValue.TryGetValue<bool>(out var active) ||
            !active)
        {
            throw new InvalidDataException(
                "SteamVR setting 'activateMultipleDrivers' was not enabled.");
        }

        var overrides = RequireObject(root, TrackingOverridesSectionName);
        if (!overrides.TryGetPropertyValue(binding.PoseSourceDevicePath, out var target) ||
            !string.Equals(
                GetOverrideTarget(binding.PoseSourceDevicePath, target),
                binding.SemanticHandPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The intended SteamVR TrackingOverrides mapping was not written.");
        }
    }

    private static void ValidateReleased(
        JsonObject root,
        TrackingOverrideBinding binding)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        if (overrides.ContainsKey(binding.PoseSourceDevicePath))
        {
            throw new InvalidDataException(
                "The intended SteamVR TrackingOverrides mapping remains active.");
        }
    }

    private static void ValidateSemanticHandReleased(
        JsonObject root,
        string semanticHandPath)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        var remainingSource = overrides.FirstOrDefault(pair =>
            pair.Value is JsonValue value &&
            value.TryGetValue<string>(out var targetPath) &&
            string.Equals(targetPath, semanticHandPath, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(remainingSource.Key))
        {
            throw new InvalidDataException(
                $"A SteamVR TrackingOverrides mapping targeting '{semanticHandPath}' remains active.");
        }
    }

    private static void ValidateApplicationSafetyOverridesReleased(
        JsonObject root,
        IReadOnlySet<string> poseSourceDevicePaths,
        string semanticHandPath)
    {
        if (!root.TryGetPropertyValue(TrackingOverridesSectionName, out var overridesNode))
        {
            return;
        }

        if (overridesNode is not JsonObject overrides)
        {
            throw WrongSectionType(TrackingOverridesSectionName);
        }

        var remainingSource = poseSourceDevicePaths.FirstOrDefault(overrides.ContainsKey);
        if (remainingSource is not null)
        {
            throw new InvalidDataException(
                $"SteamVR TrackingOverrides still references application pose source " +
                $"'{remainingSource}'.");
        }

        ValidateSemanticHandReleased(root, semanticHandPath);
    }

    private static void ValidateSemanticHandPath(string semanticHandPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticHandPath);
        if (semanticHandPath is not (
                TrackingOverrideBinding.LeftHandPath or
                TrackingOverrideBinding.RightHandPath))
        {
            throw new ArgumentException(
                $"Semantic hand path must be '{TrackingOverrideBinding.LeftHandPath}' or " +
                $"'{TrackingOverrideBinding.RightHandPath}'.",
                nameof(semanticHandPath));
        }
    }

    private static JsonObject GetOrCreateObject(
        JsonObject root,
        string propertyName,
        ref bool changed)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node))
        {
            var created = new JsonObject();
            root.Add(propertyName, created);
            changed = true;
            return created;
        }

        return node as JsonObject ?? throw WrongSectionType(propertyName);
    }

    private static JsonObject RequireObject(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonObject value)
        {
            throw WrongSectionType(propertyName);
        }

        return value;
    }

    private static InvalidDataException WrongSectionType(string propertyName) =>
        new($"SteamVR settings property '{propertyName}' must be a JSON object.");

    private static void ValidateOverrideValueTypes(JsonObject overrides)
    {
        foreach (var pair in overrides)
        {
            _ = GetOverrideTarget(pair.Key, pair.Value);
        }
    }

    private static string GetOverrideTarget(string source, JsonNode? target)
    {
        if (target is JsonValue value && value.TryGetValue<string>(out var targetPath))
        {
            return targetPath;
        }

        throw new InvalidDataException(
            $"TrackingOverrides entry '{source}' must have a string target path.");
    }

    private byte[] ReadSettingsBytes()
    {
        try
        {
            return File.ReadAllBytes(SettingsFilePath);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException(
                "The explicitly selected steamvr.vrsettings file does not exist.",
                SettingsFilePath,
                exception);
        }
    }

    private static JsonObject ParseRoot(byte[] bytes, string sourcePath)
    {
        try
        {
            var node = JsonNode.Parse(bytes);
            return node as JsonObject ?? throw new InvalidDataException(
                $"SteamVR settings '{sourcePath}' must have a JSON object root.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"SteamVR settings '{sourcePath}' are not valid JSON.",
                exception);
        }
    }

    private static byte[] Serialize(JsonObject root) =>
        Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions) + "\n");

    private void EnsureSettingsUnchanged(byte[] expectedBytes)
    {
        // The sibling lock closes this comparison-to-rename window between
        // cooperating LTB processes. An external writer that ignores the lock
        // can still write after this comparison and before File.Move. The
        // post-write ownership check prevents a later external winner from
        // being overwritten by automatic rollback, but cannot make that final
        // external-writer race atomic without SteamVR cooperation.
        if (!ReadSettingsBytes().AsSpan().SequenceEqual(expectedBytes))
        {
            throw new IOException(
                "steamvr.vrsettings changed during the update; no LTB write was performed.");
        }
    }

    private string CreateUniqueBackup(byte[] originalBytes)
    {
        var stagingPath = CreateUniqueWrittenSibling(
            SettingsFilePath + BackupWriteMarker,
            originalBytes);
        try
        {
            var prefix = SettingsFilePath + BackupMarker;
            for (var suffix = 0; suffix < MaximumUniqueSiblingAttempts; suffix++)
            {
                var candidate = suffix == 0 ? prefix : $"{prefix}.{suffix}";
                try
                {
                    File.Move(stagingPath, candidate, overwrite: false);
                    stagingPath = string.Empty;
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another operation or an earlier run owns this complete
                    // backup name. The fully flushed staging file is safe to
                    // publish under the next deterministic suffix.
                }
            }

            throw new IOException(
                $"Could not allocate a unique sibling backup after " +
                $"{MaximumUniqueSiblingAttempts} attempts.");
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingPath))
            {
                TryDelete(stagingPath);
            }
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        var temporaryPath = CreateUniqueWrittenSibling(
            SettingsFilePath + TemporaryMarker,
            bytes);
        try
        {
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static string CreateUniqueWrittenSibling(string prefix, byte[] bytes)
    {
        for (var suffix = 0; suffix < MaximumUniqueSiblingAttempts; suffix++)
        {
            var candidate = suffix == 0 ? prefix : $"{prefix}.{suffix}";
            FileStream stream;
            try
            {
                stream = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Only a CreateNew name collision is retryable. Write and
                // flush failures below propagate and are never mistaken for
                // collisions.
                continue;
            }

            try
            {
                using (stream)
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                return candidate;
            }
            catch
            {
                TryDelete(candidate);
                throw;
            }
        }

        throw new IOException(
            $"Could not allocate a unique sibling staging file after " +
            $"{MaximumUniqueSiblingAttempts} attempts.");
    }

    private FileStream AcquireOperationLock()
    {
        var lockFilePath = SettingsFilePath + LockMarker;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IOException? lastContention = null;

        while (true)
        {
            try
            {
                return new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception) when (File.Exists(lockFilePath))
            {
                lastContention = exception;
                var remaining = _lockTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new SteamVrSettingsLockException(
                        lockFilePath,
                        _lockTimeout,
                        lastContention);
                }

                Thread.Sleep(remaining < LockRetryInterval
                    ? remaining
                    : LockRetryInterval);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Staging and atomic-write names are deliberately not recognized
            // as recovery backups if best-effort cleanup is blocked.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary write/flush failure for diagnosis.
        }
    }

    private void ValidateExactBytes(byte[] expectedBytes, bool requireValidJson = true)
    {
        var actualBytes = ReadSettingsBytes();
        if (requireValidJson)
        {
            _ = ParseRoot(actualBytes, SettingsFilePath);
        }

        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new InvalidDataException(
                "Post-write validation found bytes different from the selected backup.");
        }
    }

    private void ThrowAfterAutomaticRestore(
        byte[] originalBytes,
        byte[] replacementBytes,
        string backupFilePath,
        Exception failure)
    {
        byte[] currentBytes;
        try
        {
            currentBytes = ReadSettingsBytes();
        }
        catch (Exception ownershipCheckFailure)
        {
            throw new SteamVrSettingsUpdateException(
                "The SteamVR settings update failed and LTB could not verify that " +
                "it still owned the target. Automatic restoration was not attempted. " +
                $"Recover from '{backupFilePath}'.",
                backupFilePath,
                originalRestored: false,
                new AggregateException(failure, ownershipCheckFailure));
        }

        if (!currentBytes.AsSpan().SequenceEqual(replacementBytes))
        {
            throw new SteamVrSettingsUpdateException(
                "The SteamVR settings update failed, but the target changed after " +
                "LTB replaced it. Automatic restoration was not attempted because " +
                "that would overwrite the later writer. " +
                $"Recover from '{backupFilePath}' after resolving the concurrent writer.",
                backupFilePath,
                originalRestored: false,
                failure);
        }

        try
        {
            WriteAtomically(originalBytes);
            ValidateExactBytes(originalBytes, requireValidJson: false);
        }
        catch (Exception restoreFailure)
        {
            throw new SteamVrSettingsUpdateException(
                "The SteamVR settings update failed and automatic restoration also failed. " +
                $"Recover from '{backupFilePath}'.",
                backupFilePath,
                originalRestored: false,
                new AggregateException(failure, restoreFailure));
        }

        throw new SteamVrSettingsUpdateException(
            "The SteamVR settings update failed; the original settings were restored.",
            backupFilePath,
            originalRestored: true,
            failure);
    }

    private string ValidateBackupPath(string backupFilePath)
    {
        var fullBackupPath = Path.GetFullPath(backupFilePath);
        if (!PathsEqual(Path.GetDirectoryName(fullBackupPath), GetSettingsDirectory()))
        {
            throw new ArgumentException(
                "The recovery backup must be a sibling of steamvr.vrsettings.",
                nameof(backupFilePath));
        }

        var backupPrefix = Path.GetFileName(SettingsFilePath) + BackupMarker;
        if (!TryParseRecognizedBackupName(
                Path.GetFileName(fullBackupPath),
                backupPrefix,
                out _))
        {
            throw new ArgumentException(
                "The file is not a recovery backup created for this settings path.",
                nameof(backupFilePath));
        }

        var attributes = File.GetAttributes(fullBackupPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new ArgumentException(
                "The recovery backup must be a regular sibling file, not a " +
                "directory, symbolic link, or reparse point.",
                nameof(backupFilePath));
        }

        return fullBackupPath;
    }

    private static bool TryParseRecognizedBackupName(
        string fileName,
        string backupPrefix,
        out int sequenceNumber)
    {
        sequenceNumber = 0;
        if (string.Equals(fileName, backupPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (!fileName.StartsWith(backupPrefix + ".", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileName[(backupPrefix.Length + 1)..];
        return int.TryParse(suffix, out sequenceNumber) &&
            sequenceNumber > 0 &&
            string.Equals(
                sequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                suffix,
                StringComparison.Ordinal);
    }

    private static bool TryGetSafeRecoveryCandidate(
        string path,
        string fileName,
        int sequenceNumber,
        out SteamVrSettingsRecoveryCandidate candidate)
    {
        candidate = null!;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            var file = new FileInfo(path);
            candidate = new SteamVrSettingsRecoveryCandidate(
                file.FullName,
                fileName,
                sequenceNumber,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                DirectoryNotFoundException or
                UnauthorizedAccessException or
                IOException)
        {
            // A candidate that disappeared or whose safe metadata cannot be
            // read is omitted. Discovery never opens it to inspect content.
            return false;
        }
    }

    private string GetSettingsDirectory() =>
        Path.GetDirectoryName(SettingsFilePath)
        ?? throw new InvalidOperationException("Settings path has no parent directory.");

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
