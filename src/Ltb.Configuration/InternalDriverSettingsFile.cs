using System.Diagnostics;
using System.Security.Cryptography;
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
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(20);
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
        using var settingsLock = AcquireExclusiveLock(canonicalPath);
        AtomicFileWriter.Write(canonicalPath, InternalDriverSettingsJson.Serialize(settings));
    }

    /// <summary>Computes the exact SHA-256 generation of the current settings bytes.</summary>
    public static string ComputeGeneration(string path)
    {
        var canonicalPath = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            path,
            nameof(path));
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonicalPath)));
    }

    /// <summary>
    /// Atomically compares the current settings generation and replaces the
    /// complete file only when it still matches. All LTB settings writers use
    /// the same cross-process lock; a mismatch performs no write.
    /// </summary>
    public static bool TrySaveIfGenerationMatches(
        string path,
        string expectedGeneration,
        InternalDriverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var canonicalPath = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            path,
            nameof(path));
        var canonicalGeneration = RequireGeneration(expectedGeneration);
        using var settingsLock = AcquireExclusiveLock(canonicalPath);
        if (!string.Equals(
                ComputeGeneration(canonicalPath),
                canonicalGeneration,
                StringComparison.Ordinal))
        {
            return false;
        }

        AtomicFileWriter.Write(canonicalPath, InternalDriverSettingsJson.Serialize(settings));
        return true;
    }

    private static string RequireGeneration(string generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        if (generation.Length != 64 ||
            generation.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Settings generation must be a SHA-256 value.",
                nameof(generation));
        }

        return generation.ToUpperInvariant();
    }

    private static FileStream AcquireExclusiveLock(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new ArgumentException(
                "Settings path must have a parent directory.",
                nameof(settingsPath));
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(
            directory,
            $".{Path.GetFileName(settingsPath)}.lock");
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception) when (
                DriverRegistrationReceiptStore.IsLockContention(exception))
            {
                lastContention = exception;
            }

            var remaining = LockTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Timed out after {LockTimeout.TotalMilliseconds:F0} ms waiting for " +
                    $"the internal-driver settings lock '{lockPath}'.",
                    lastContention);
            }

            Thread.Sleep(remaining < LockRetryDelay ? remaining : LockRetryDelay);
        }
    }
}
