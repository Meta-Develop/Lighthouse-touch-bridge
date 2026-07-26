using System.Security;
using System.Text.Json;

namespace Ltb.Driver;

public sealed class PairedLighthouseDeviceDiscovery
{
    private const string GenericTrackerDeviceClass = "generic_tracker";
    private const string LighthousePairingDirectoryName = "lighthouse";

    private readonly SteamVrPathDiscovery _pathDiscovery;
    private readonly IPairedLighthouseDeviceFileSystem _fileSystem;

    public PairedLighthouseDeviceDiscovery(
        ISteamVrHostEnvironment environment,
        ISteamVrFileSystem steamVrFileSystem)
        : this(
            new SteamVrPathDiscovery(environment, steamVrFileSystem),
            new SystemPairedLighthouseDeviceFileSystem())
    {
    }

    public PairedLighthouseDeviceDiscovery(
        SteamVrPathDiscovery pathDiscovery,
        IPairedLighthouseDeviceFileSystem fileSystem)
    {
        _pathDiscovery = pathDiscovery ??
            throw new ArgumentNullException(nameof(pathDiscovery));
        _fileSystem = fileSystem ??
            throw new ArgumentNullException(nameof(fileSystem));
    }

    public async ValueTask<PairedLighthouseDeviceDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var configRoots = await _pathDiscovery
            .DiscoverConfigRootsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!configRoots.IsSuccess)
        {
            return Failure(
                configRoots.DiagnosticCode,
                configRoots.Diagnostic,
                configRoots.OpenVrPathsFile);
        }

        var devices = new List<PairedLighthouseDevice>();
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? firstMissingLighthouseDirectory = null;
        var applicableConfigRootCount = 0;
        foreach (var configRoot in configRoots.ConfigRoots)
        {
            string lighthouseDirectory;
            try
            {
                lighthouseDirectory = _fileSystem.GetCanonicalPath(
                    Path.Combine(configRoot, LighthousePairingDirectoryName));
                if (!_fileSystem.DirectoryExists(lighthouseDirectory))
                {
                    firstMissingLighthouseDirectory ??= lighthouseDirectory;
                    continue;
                }

                applicableConfigRootCount++;
            }
            catch (Exception exception) when (IsFileAccessFailure(exception))
            {
                return Failure(
                    PairedLighthouseDeviceDiagnosticCode.LighthouseDirectoryUnreadable,
                    $"The exact '{LighthousePairingDirectoryName}' paired-device directory " +
                    $"could not be inspected for config root '{configRoot}'.",
                    configRoot);
            }

            IReadOnlyList<string> deviceDirectories;
            try
            {
                deviceDirectories = _fileSystem
                    .EnumerateDirectories(lighthouseDirectory)
                    .Select(_fileSystem.GetCanonicalPath)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (IsFileAccessFailure(exception))
            {
                return Failure(
                    PairedLighthouseDeviceDiagnosticCode.LighthouseDirectoryUnreadable,
                    $"The exact '{LighthousePairingDirectoryName}' paired-device directory " +
                    $"could not be enumerated: '{lighthouseDirectory}'.",
                    lighthouseDirectory);
            }

            foreach (var deviceDirectory in deviceDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string configFile;
                try
                {
                    configFile = _fileSystem.GetCanonicalPath(
                        Path.Combine(deviceDirectory, "config.json"));
                    if (!_fileSystem.FileExists(configFile))
                    {
                        return Failure(
                            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMissing,
                            $"A device directory under the exact " +
                            $"'{LighthousePairingDirectoryName}' paired-device directory " +
                            $"has no config.json: '{deviceDirectory}'.",
                            configFile);
                    }
                }
                catch (Exception exception) when (IsFileAccessFailure(exception))
                {
                    return Failure(
                        PairedLighthouseDeviceDiagnosticCode.DeviceConfigUnreadable,
                        $"A Lighthouse device config could not be inspected under " +
                        $"'{deviceDirectory}'.",
                        deviceDirectory);
                }

                string json;
                try
                {
                    json = await _fileSystem
                        .ReadAllTextAsync(configFile, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileAccessFailure(exception))
                {
                    return Failure(
                        PairedLighthouseDeviceDiagnosticCode.DeviceConfigUnreadable,
                        $"The Lighthouse device config could not be read: " +
                        $"'{configFile}'.",
                        configFile);
                }

                var parsed = ParseConfig(json, configFile);
                if (parsed.Failure is not null)
                {
                    return parsed.Failure;
                }

                if (parsed.Device is null)
                {
                    continue;
                }

                if (!serials.Add(parsed.Device.Serial))
                {
                    return Failure(
                        PairedLighthouseDeviceDiagnosticCode.DuplicateTrackerSerial,
                        $"Multiple Lighthouse device configs declare tracker serial " +
                        $"'{parsed.Device.Serial}'.",
                        configFile);
                }

                devices.Add(parsed.Device);
            }
        }

        if (applicableConfigRootCount == 0)
        {
            return Failure(
                PairedLighthouseDeviceDiagnosticCode.LighthouseDirectoryMissing,
                $"No configured SteamVR config root contains the exact " +
                $"'{LighthousePairingDirectoryName}' paired-device directory.",
                firstMissingLighthouseDirectory ?? configRoots.OpenVrPathsFile);
        }

        if (devices.Count == 0)
        {
            return Failure(
                PairedLighthouseDeviceDiagnosticCode.TrackerEnumerationEmpty,
                $"No paired generic Lighthouse trackers were found in the exact " +
                $"'{LighthousePairingDirectoryName}' directories under the configured roots.",
                configRoots.OpenVrPathsFile);
        }

        var orderedDevices = devices
            .OrderBy(device => device.Serial, StringComparer.Ordinal)
            .ThenBy(device => device.Model, StringComparer.Ordinal)
            .ToArray();
        return new PairedLighthouseDeviceDiscoveryResult(
            PairedLighthouseDeviceDiagnosticCode.None,
            $"Discovered {orderedDevices.Length} paired generic Lighthouse tracker(s).",
            orderedDevices);
    }

    private static (
        PairedLighthouseDevice? Device,
        PairedLighthouseDeviceDiscoveryResult? Failure)
        ParseConfig(string json, string configFile)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredString(root, "device_class", out var deviceClass))
            {
                return (null, Malformed(
                    configFile,
                    "required 'device_class' string is missing or blank"));
            }

            if (!TryGetRequiredString(
                    root,
                    "device_serial_number",
                    out var serial))
            {
                return (null, Malformed(
                    configFile,
                    "required 'device_serial_number' string is missing or blank"));
            }

            if (!TryGetRequiredString(root, "model_number", out var model))
            {
                return (null, Malformed(
                    configFile,
                    "required 'model_number' string is missing or blank"));
            }

            if (!string.Equals(
                    deviceClass,
                    GenericTrackerDeviceClass,
                    StringComparison.Ordinal))
            {
                return (null, null);
            }

            return (new PairedLighthouseDevice(serial, model), null);
        }
        catch (JsonException)
        {
            return (null, Malformed(configFile, "JSON is malformed"));
        }
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            value = text.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static PairedLighthouseDeviceDiscoveryResult Malformed(
        string configFile,
        string reason) =>
        Failure(
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed,
            $"Lighthouse device config '{configFile}' is invalid: {reason}.",
            configFile);

    private static PairedLighthouseDeviceDiscoveryResult Failure(
        PairedLighthouseDeviceDiagnosticCode code,
        string diagnostic,
        string? path) =>
        new(code, diagnostic, Array.Empty<PairedLighthouseDevice>(), path);

    private static bool IsFileAccessFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
}
