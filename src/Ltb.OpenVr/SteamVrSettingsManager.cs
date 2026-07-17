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

    /// <summary>Lists manager-created sibling backups in ordinal name order.</summary>
    public IReadOnlyList<string> FindRecoveryBackups()
    {
        var directory = GetSettingsDirectory();
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var backupPrefix = Path.GetFileName(SettingsFilePath) + BackupMarker;
        return Directory
            .EnumerateFiles(directory, backupPrefix + "*")
            .Where(path => IsRecognizedBackupName(Path.GetFileName(path), backupPrefix))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private SteamVrSettingsRecoveryPoint ApplyJsonMutation(
        SteamVrSettingsOperation operation,
        TrackingOverrideBinding binding,
        Func<JsonObject, bool> mutate,
        Action<JsonObject> validateOperation)
    {
        var originalBytes = ReadSettingsBytes();
        var root = ParseRoot(originalBytes, SettingsFilePath);
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
                expectedPostImage: null);
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
                expectedPostImage: updatedBytes);
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
        if (!IsRecognizedBackupName(Path.GetFileName(fullBackupPath), backupPrefix))
        {
            throw new ArgumentException(
                "The file is not a recovery backup created for this settings path.",
                nameof(backupFilePath));
        }

        return fullBackupPath;
    }

    private static bool IsRecognizedBackupName(string fileName, string backupPrefix)
    {
        if (string.Equals(fileName, backupPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (!fileName.StartsWith(backupPrefix + ".", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileName[(backupPrefix.Length + 1)..];
        return int.TryParse(suffix, out var number) && number > 0;
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
