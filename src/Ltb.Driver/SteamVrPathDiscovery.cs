using System.Security;
using System.Text.Json;

namespace Ltb.Driver;

public sealed class SteamVrPathDiscovery
{
    private readonly ISteamVrHostEnvironment _environment;
    private readonly ISteamVrFileSystem _fileSystem;

    public SteamVrPathDiscovery(
        ISteamVrHostEnvironment environment,
        ISteamVrFileSystem fileSystem)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async ValueTask<SteamVrPaths> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_environment.IsWindows)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.PlatformUnsupported,
                "SteamVR path discovery requires Windows LocalApplicationData.");
        }

        var localApplicationData = _environment.GetLocalApplicationDataPath();
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.LocalApplicationDataUnavailable,
                "The current user's Windows LocalApplicationData path is unavailable.");
        }

        var openVrPathsFile = _fileSystem.GetCanonicalPath(
            Path.Combine(localApplicationData, "openvr", "openvrpaths.vrpath"));
        if (!_fileSystem.FileExists(openVrPathsFile))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsMissing,
                $"The current user's OpenVR path registry does not exist: '{openVrPathsFile}'.");
        }

        var json = await _fileSystem.ReadAllTextAsync(
            openVrPathsFile,
            cancellationToken).ConfigureAwait(false);
        var document = ParseOpenVrPaths(json, openVrPathsFile);
        var runtimeRoot = _fileSystem.GetCanonicalPath(
            RequireFirstPath(document, "runtime", openVrPathsFile));
        var configRoot = _fileSystem.GetCanonicalPath(
            RequireFirstPath(document, "config", openVrPathsFile));
        var vrPathRegExecutable = _fileSystem.GetCanonicalPath(
            Path.Combine(runtimeRoot, "bin", "win64", "vrpathreg.exe"));
        var settingsFile = _fileSystem.GetCanonicalPath(
            Path.Combine(configRoot, "steamvr.vrsettings"));

        if (!_fileSystem.FileExists(vrPathRegExecutable))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.VrPathRegMissing,
                $"The registered SteamVR runtime has no win64 vrpathreg.exe: '{vrPathRegExecutable}'.");
        }

        if (!_fileSystem.FileExists(settingsFile))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.SteamVrSettingsMissing,
                $"The registered SteamVR config has no steamvr.vrsettings: '{settingsFile}'.");
        }

        return new SteamVrPaths(
            openVrPathsFile,
            runtimeRoot,
            configRoot,
            vrPathRegExecutable,
            settingsFile);
    }

    public async ValueTask<SteamVrConfigRootDiscoveryResult> DiscoverConfigRootAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await DiscoverConfigRootsAsync(cancellationToken).ConfigureAwait(false);
        return new SteamVrConfigRootDiscoveryResult(
            result.DiagnosticCode,
            result.Diagnostic,
            result.OpenVrPathsFile,
            result.IsSuccess ? result.ConfigRoots[0] : null);
    }

    public async ValueTask<SteamVrConfigRootsDiscoveryResult> DiscoverConfigRootsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_environment.IsWindows)
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.PlatformUnsupported,
                "SteamVR config-root discovery requires Windows LocalApplicationData.");
        }

        var localApplicationData = _environment.GetLocalApplicationDataPath();
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.LocalApplicationDataUnavailable,
                "The current user's Windows LocalApplicationData path is unavailable.");
        }

        string openVrPathsFile;
        try
        {
            openVrPathsFile = _fileSystem.GetCanonicalPath(
                Path.Combine(localApplicationData, "openvr", "openvrpaths.vrpath"));
            if (!_fileSystem.FileExists(openVrPathsFile))
            {
                return ConfigRootsFailure(
                    PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMissing,
                    $"The current user's OpenVR path registry does not exist: " +
                    $"'{openVrPathsFile}'.",
                    openVrPathsFile);
            }
        }
        catch (Exception exception) when (IsPathOrFileAccessFailure(exception))
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.OpenVrPathsUnreadable,
                "The current user's OpenVR path registry location could not be inspected.");
        }

        string json;
        try
        {
            json = await _fileSystem
                .ReadAllTextAsync(openVrPathsFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsPathOrFileAccessFailure(exception))
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.OpenVrPathsUnreadable,
                $"The current user's OpenVR path registry could not be read: " +
                $"'{openVrPathsFile}'.",
                openVrPathsFile);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMalformed,
                $"OpenVR path registry '{openVrPathsFile}' is not valid JSON.",
                openVrPathsFile);
        }

        if (!TryGetPaths(root, "config", out var configRoots))
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.ConfigRootUnavailable,
                $"OpenVR path registry has no usable 'config' roots in " +
                $"'{openVrPathsFile}'.",
                openVrPathsFile);
        }

        string[] canonicalConfigRoots;
        try
        {
            canonicalConfigRoots = configRoots
                .Select(_fileSystem.GetCanonicalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsPathOrFileAccessFailure(exception))
        {
            return ConfigRootsFailure(
                PairedLighthouseDeviceDiagnosticCode.ConfigRootUnavailable,
                $"OpenVR path registry has an unusable 'config' root in " +
                $"'{openVrPathsFile}'.",
                openVrPathsFile);
        }

        return new SteamVrConfigRootsDiscoveryResult(
            PairedLighthouseDeviceDiagnosticCode.None,
            $"Discovered {canonicalConfigRoots.Length} SteamVR config root(s).",
            openVrPathsFile,
            canonicalConfigRoots);
    }

    private static JsonElement ParseOpenVrPaths(string json, string sourcePath)
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
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                $"OpenVR path registry '{sourcePath}' is not valid JSON.",
                exception);
        }
    }

    private static string RequireFirstPath(
        JsonElement root,
        string propertyName,
        string sourcePath)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var paths) ||
            paths.ValueKind != JsonValueKind.Array)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                $"OpenVR path registry is invalid: required '{propertyName}' array " +
                $"is missing or is not an array in '{sourcePath}'.");
        }

        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind == JsonValueKind.String &&
                path.GetString() is { } value &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw Failure(
            SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
            $"OpenVR path registry is invalid: required '{propertyName}' array " +
            $"has no usable path in '{sourcePath}'.");
    }

    private static bool TryGetFirstPath(
        JsonElement root,
        string propertyName,
        out string value)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var paths) &&
            paths.ValueKind == JsonValueKind.Array)
        {
            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind == JsonValueKind.String &&
                    path.GetString() is { } candidate &&
                    !string.IsNullOrWhiteSpace(candidate))
                {
                    value = candidate;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPaths(
        JsonElement root,
        string propertyName,
        out IReadOnlyList<string> values)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var paths) &&
            paths.ValueKind == JsonValueKind.Array)
        {
            var usablePaths = paths
                .EnumerateArray()
                .Where(path => path.ValueKind == JsonValueKind.String)
                .Select(path => path.GetString())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToArray();
            if (usablePaths.Length > 0)
            {
                values = usablePaths;
                return true;
            }
        }

        values = Array.Empty<string>();
        return false;
    }

    private static SteamVrConfigRootDiscoveryResult ConfigRootFailure(
        PairedLighthouseDeviceDiagnosticCode code,
        string diagnostic,
        string? openVrPathsFile = null) =>
        new(code, diagnostic, openVrPathsFile, null);

    private static SteamVrConfigRootsDiscoveryResult ConfigRootsFailure(
        PairedLighthouseDeviceDiagnosticCode code,
        string diagnostic,
        string? openVrPathsFile = null) =>
        new(code, diagnostic, openVrPathsFile, Array.Empty<string>());

    private static bool IsPathOrFileAccessFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;

    private static SteamVrDriverLifecycleException Failure(
        SteamVrDriverDiagnosticCode code,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new SteamVrDriverLifecycleException(code, message)
            : new SteamVrDriverLifecycleException(code, message, innerException);
}
