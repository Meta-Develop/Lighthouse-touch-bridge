namespace Ltb.Driver;

public enum PairedLighthouseDeviceDiagnosticCode
{
    None = 0,
    PlatformUnsupported,
    LocalApplicationDataUnavailable,
    OpenVrPathsMissing,
    OpenVrPathsUnreadable,
    OpenVrPathsMalformed,
    ConfigRootUnavailable,
    LighthouseDirectoryMissing,
    LighthouseDirectoryUnreadable,
    DeviceConfigMissing,
    DeviceConfigUnreadable,
    DeviceConfigMalformed,
    DuplicateTrackerSerial,
    TrackerEnumerationEmpty,
}

public sealed record SteamVrConfigRootDiscoveryResult(
    PairedLighthouseDeviceDiagnosticCode DiagnosticCode,
    string Diagnostic,
    string? OpenVrPathsFile,
    string? ConfigRoot)
{
    public bool IsSuccess =>
        DiagnosticCode == PairedLighthouseDeviceDiagnosticCode.None;
}

public sealed record PairedLighthouseDevice
{
    public PairedLighthouseDevice(string serial, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        Serial = serial.Trim().ToUpperInvariant();
        Model = model.Trim();
    }

    public string Serial { get; }

    public string Model { get; }

    public bool HasSerial(string serial) =>
        string.Equals(Serial, serial?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record PairedLighthouseDeviceDiscoveryResult(
    PairedLighthouseDeviceDiagnosticCode DiagnosticCode,
    string Diagnostic,
    IReadOnlyList<PairedLighthouseDevice> Devices,
    string? FailurePath = null)
{
    public bool IsSuccess =>
        DiagnosticCode == PairedLighthouseDeviceDiagnosticCode.None;
}

public interface IPairedLighthouseDeviceFileSystem
{
    string GetCanonicalPath(string path);

    bool DirectoryExists(string path);

    IReadOnlyList<string> EnumerateDirectories(string path);

    bool FileExists(string path);

    ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed class SystemPairedLighthouseDeviceFileSystem :
    IPairedLighthouseDeviceFileSystem
{
    public string GetCanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path) =>
        Directory.EnumerateDirectories(path)
            .Select(GetCanonicalPath)
            .ToArray();

    public bool FileExists(string path) => File.Exists(path);

    public ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken) =>
        new(File.ReadAllTextAsync(path, cancellationToken));
}
