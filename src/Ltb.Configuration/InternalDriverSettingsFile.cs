using System.Text;

namespace Ltb.Configuration;

/// <summary>The outcome of loading a settings file that may not exist on first run.</summary>
public enum InternalDriverSettingsLoadStatus
{
    Loaded,
    NotFound,
}

/// <summary>Distinguishes first-run absence from malformed or unreadable settings.</summary>
public sealed record InternalDriverSettingsLoadResult
{
    private InternalDriverSettingsLoadResult(
        InternalDriverSettingsLoadStatus status,
        InternalDriverSettings? settings)
    {
        Status = status;
        Settings = settings;
    }

    public InternalDriverSettingsLoadStatus Status { get; }

    public InternalDriverSettings? Settings { get; }

    internal static InternalDriverSettingsLoadResult Loaded(InternalDriverSettings settings) =>
        new(InternalDriverSettingsLoadStatus.Loaded, settings);

    internal static InternalDriverSettingsLoadResult NotFound { get; } =
        new(InternalDriverSettingsLoadStatus.NotFound, settings: null);
}

/// <summary>Loads and atomically saves internal-driver application settings.</summary>
public static class InternalDriverSettingsFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static InternalDriverSettingsLoadResult TryLoad(string path)
    {
        var canonicalPath = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            path,
            nameof(path));

        try
        {
            var json = File.ReadAllText(canonicalPath, Utf8WithoutBom);
            return InternalDriverSettingsLoadResult.Loaded(
                InternalDriverSettingsJson.Deserialize(json));
        }
        catch (FileNotFoundException)
        {
            return InternalDriverSettingsLoadResult.NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return InternalDriverSettingsLoadResult.NotFound;
        }
    }

    public static InternalDriverSettings Load(string path)
    {
        var result = TryLoad(path);
        return result.Settings ?? throw new FileNotFoundException(
            "Internal-driver settings file was not found on first run.",
            path);
    }

    public static void Save(string path, InternalDriverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var canonicalPath = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            path,
            nameof(path));
        AtomicFileWriter.Write(canonicalPath, InternalDriverSettingsJson.Serialize(settings));
    }
}
