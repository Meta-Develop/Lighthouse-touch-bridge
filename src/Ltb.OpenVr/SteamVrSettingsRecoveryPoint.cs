namespace Ltb.OpenVr;

/// <summary>The operation that created a SteamVR settings recovery point.</summary>
public enum SteamVrSettingsOperation
{
    EnableTrackingOverride = 0,
    ReleaseTrackingOverride = 1,
    RestoreBackup = 2,
    ReleaseSemanticHandOverrides = 3,
    ReleaseApplicationSafetyOverrides = 4,
}

/// <summary>
/// Describes a completed settings operation and the sibling backup that can
/// restore the bytes that existed immediately before it.
/// </summary>
public sealed class SteamVrSettingsRecoveryPoint
{
    internal SteamVrSettingsRecoveryPoint(
        string settingsFilePath,
        string? backupFilePath,
        SteamVrSettingsOperation operation,
        TrackingOverrideBinding? binding,
        bool settingsChanged,
        byte[]? expectedPostImage)
    {
        SettingsFilePath = settingsFilePath;
        BackupFilePath = backupFilePath;
        Operation = operation;
        Binding = binding;
        SettingsChanged = settingsChanged;
        ExpectedPostImage = expectedPostImage?.ToArray();
    }

    public string SettingsFilePath { get; }

    /// <summary>
    /// Unique sibling backup, or <see langword="null"/> when the requested
    /// operation was already satisfied and no file mutation occurred.
    /// </summary>
    public string? BackupFilePath { get; }

    public SteamVrSettingsOperation Operation { get; }

    public TrackingOverrideBinding? Binding { get; }

    public bool SettingsChanged { get; }

    /// <summary>
    /// Exact bytes written by the operation. Explicit rollback may restore the
    /// backup only while the current file still equals this owned post-image.
    /// </summary>
    internal byte[]? ExpectedPostImage { get; }
}

/// <summary>
/// Reports a failed settings update and whether its pre-update bytes were
/// restored successfully.
/// </summary>
public sealed class SteamVrSettingsUpdateException : IOException
{
    internal SteamVrSettingsUpdateException(
        string message,
        string backupFilePath,
        bool originalRestored,
        Exception innerException)
        : base(message, innerException)
    {
        BackupFilePath = backupFilePath;
        OriginalRestored = originalRestored;
    }

    public string BackupFilePath { get; }

    public bool OriginalRestored { get; }
}

/// <summary>
/// Indicates that another LTB process retained exclusive ownership of the
/// sibling settings-operation lock for the complete bounded wait.
/// </summary>
public sealed class SteamVrSettingsLockException : IOException
{
    internal SteamVrSettingsLockException(
        string lockFilePath,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"Another LTB process is updating SteamVR settings. " +
            $"Could not acquire '{lockFilePath}' within {timeout.TotalMilliseconds:0} ms.",
            innerException)
    {
        LockFilePath = lockFilePath;
        Timeout = timeout;
    }

    public string LockFilePath { get; }

    public TimeSpan Timeout { get; }
}
