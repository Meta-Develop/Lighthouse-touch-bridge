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
                $"OpenVR path registry '{sourcePath}' has no '{propertyName}' array.");
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
            $"OpenVR path registry '{sourcePath}' has no usable '{propertyName}' path.");
    }

    private static SteamVrDriverLifecycleException Failure(
        SteamVrDriverDiagnosticCode code,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new SteamVrDriverLifecycleException(code, message)
            : new SteamVrDriverLifecycleException(code, message, innerException);
}
