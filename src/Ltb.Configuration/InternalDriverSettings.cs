namespace Ltb.Configuration;

/// <summary>Constants for the persisted internal-driver application settings schema.</summary>
public static class InternalDriverSettingsSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>How the application locates SteamVR's openvrpaths file.</summary>
public enum OpenVrPathsDiscoveryMode
{
    Automatic,
    ExplicitFile,
}

/// <summary>
/// Explicit automatic discovery or one caller-supplied canonical file path.
/// </summary>
public sealed record OpenVrPathsDiscovery
{
    private OpenVrPathsDiscovery(OpenVrPathsDiscoveryMode mode, string? filePath)
    {
        Mode = mode;
        FilePath = filePath;
    }

    public static OpenVrPathsDiscovery Automatic { get; } = new(
        OpenVrPathsDiscoveryMode.Automatic,
        filePath: null);

    public OpenVrPathsDiscoveryMode Mode { get; }

    public string? FilePath { get; }

    public static OpenVrPathsDiscovery FromFile(string filePath) => new(
        OpenVrPathsDiscoveryMode.ExplicitFile,
        SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            filePath,
            nameof(filePath)));
}

/// <summary>
/// Complete owner-selected left/right physical tracker binding persisted with
/// internal-driver settings rather than calibration profiles.
/// </summary>
public sealed record InternalDriverTrackerBinding
{
    public InternalDriverTrackerBinding(
        string leftTrackerSerial,
        string rightTrackerSerial)
    {
        LeftTrackerSerial = RequireCanonicalSerial(
            leftTrackerSerial,
            nameof(leftTrackerSerial));
        RightTrackerSerial = RequireCanonicalSerial(
            rightTrackerSerial,
            nameof(rightTrackerSerial));
        if (string.Equals(
                LeftTrackerSerial,
                RightTrackerSerial,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Manual left and right tracker serials must identify distinct trackers.",
                nameof(rightTrackerSerial));
        }
    }

    public string LeftTrackerSerial { get; }

    public string RightTrackerSerial { get; }

    public bool ContainsSerial(string trackerSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        var candidate = trackerSerial.Trim();
        return string.Equals(
                LeftTrackerSerial,
                candidate,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                RightTrackerSerial,
                candidate,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireCanonicalSerial(
        string trackerSerial,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial, parameterName);
        return trackerSerial.Trim().ToUpperInvariant();
    }
}

/// <summary>
/// Narrow persisted boundary needed to start the internal driver without
/// interactive path input. It intentionally provides no owner-local defaults.
/// </summary>
public sealed record InternalDriverSettings
{
    public InternalDriverSettings(
        int schemaVersion,
        OpenVrPathsDiscovery openVrPathsDiscovery,
        string stagedDriverRoot,
        string calibrationProfileStorePath,
        InternalDriverTrackerBinding? manualTrackerBinding = null,
        bool unregisterOnExit = true,
        string? trackerPathObservationStorePath = null)
    {
        if (schemaVersion != InternalDriverSettingsSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Only internal-driver settings schema version {InternalDriverSettingsSchema.CurrentVersion} is supported.");
        }

        SchemaVersion = schemaVersion;
        OpenVrPathsDiscovery = openVrPathsDiscovery
            ?? throw new ArgumentNullException(nameof(openVrPathsDiscovery));
        StagedDriverRoot = SettingsPathValidation.RequireCanonicalAbsoluteDirectoryPath(
            stagedDriverRoot,
            nameof(stagedDriverRoot));
        CalibrationProfileStorePath = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            calibrationProfileStorePath,
            nameof(calibrationProfileStorePath));
        ManualTrackerBinding = manualTrackerBinding;
        UnregisterOnExit = unregisterOnExit;
        TrackerPathObservationStorePath = trackerPathObservationStorePath is null
            ? null
            : SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
                trackerPathObservationStorePath,
                nameof(trackerPathObservationStorePath));
    }

    public int SchemaVersion { get; }

    public OpenVrPathsDiscovery OpenVrPathsDiscovery { get; }

    public string StagedDriverRoot { get; }

    public string CalibrationProfileStorePath { get; }

    public InternalDriverTrackerBinding? ManualTrackerBinding { get; }

    /// <summary>
    /// Whether controlled Stop/application exit removes receipt-owned
    /// <c>driver_ltb</c> registrations. The compatibility default is enabled.
    /// </summary>
    public bool UnregisterOnExit { get; }

    /// <summary>
    /// Optional private store containing live-observed tracker serial to exact
    /// OpenVR registered-device-path evidence. No default is invented here.
    /// </summary>
    public string? TrackerPathObservationStorePath { get; }

    public InternalDriverSettings WithStagedDriverRoot(string stagedDriverRoot) =>
        new(
            SchemaVersion,
            OpenVrPathsDiscovery,
            stagedDriverRoot,
            CalibrationProfileStorePath,
            ManualTrackerBinding,
            UnregisterOnExit,
            TrackerPathObservationStorePath);

    public InternalDriverSettings WithManualTrackerBinding(
        InternalDriverTrackerBinding? manualTrackerBinding) =>
        new(
            SchemaVersion,
            OpenVrPathsDiscovery,
            StagedDriverRoot,
            CalibrationProfileStorePath,
            manualTrackerBinding,
            UnregisterOnExit,
            TrackerPathObservationStorePath);

    public InternalDriverSettings WithUnregisterOnExit(bool unregisterOnExit) =>
        new(
            SchemaVersion,
            OpenVrPathsDiscovery,
            StagedDriverRoot,
            CalibrationProfileStorePath,
            ManualTrackerBinding,
            unregisterOnExit,
            TrackerPathObservationStorePath);

    public InternalDriverSettings WithTrackerPathObservationStorePath(
        string? trackerPathObservationStorePath) =>
        new(
            SchemaVersion,
            OpenVrPathsDiscovery,
            StagedDriverRoot,
            CalibrationProfileStorePath,
            ManualTrackerBinding,
            UnregisterOnExit,
            trackerPathObservationStorePath);
}

internal static class SettingsPathValidation
{
    internal static string RequireCanonicalAbsoluteDirectoryPath(
        string path,
        string parameterName) =>
        RequireCanonicalAbsolutePath(path, parameterName, isDirectory: true);

    internal static string RequireCanonicalAbsoluteFilePath(
        string path,
        string parameterName) =>
        RequireCanonicalAbsolutePath(path, parameterName, isDirectory: false);

    private static string RequireCanonicalAbsolutePath(
        string path,
        string parameterName,
        bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Path must be absolute and fully qualified.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        var canonicalPath = isDirectory && fullPath != Path.GetPathRoot(fullPath)
            ? Path.TrimEndingDirectorySeparator(fullPath)
            : fullPath;

        if (!string.Equals(path, canonicalPath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Path must already be canonical; canonical form is '{canonicalPath}'.",
                parameterName);
        }

        if (!isDirectory &&
            (Path.EndsInDirectorySeparator(path) || string.IsNullOrEmpty(Path.GetFileName(path))))
        {
            throw new ArgumentException("File path must identify a file, not a directory.", parameterName);
        }

        return path;
    }
}
