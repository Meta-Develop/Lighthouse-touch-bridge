namespace Ltb.OpenVr;

/// <summary>
/// Safe presentation metadata for one recognized sibling settings backup.
/// Candidate discovery never reads or exposes the backup's contents.
/// </summary>
public sealed class SteamVrSettingsRecoveryCandidate
{
    internal SteamVrSettingsRecoveryCandidate(
        string backupFilePath,
        string fileName,
        int sequenceNumber,
        long lengthBytes,
        DateTimeOffset lastWriteTimeUtc)
    {
        BackupFilePath = backupFilePath;
        FileName = fileName;
        SequenceNumber = sequenceNumber;
        LengthBytes = lengthBytes;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public string BackupFilePath { get; }

    public string FileName { get; }

    /// <summary>
    /// Zero for the unsuffixed <c>.ltb-backup</c> name and the positive
    /// canonical decimal suffix for a numbered backup.
    /// </summary>
    public int SequenceNumber { get; }

    public long LengthBytes { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }
}

/// <summary>
/// Read-only discovery result for recognized backups adjacent to one exact
/// <c>steamvr.vrsettings</c> path.
/// </summary>
public sealed class SteamVrSettingsRecoveryDiscovery
{
    internal SteamVrSettingsRecoveryDiscovery(
        string settingsFilePath,
        IEnumerable<SteamVrSettingsRecoveryCandidate> candidates)
    {
        SettingsFilePath = settingsFilePath;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public string SettingsFilePath { get; }

    public IReadOnlyList<SteamVrSettingsRecoveryCandidate> Candidates { get; }
}
