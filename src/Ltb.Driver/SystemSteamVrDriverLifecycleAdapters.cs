using System.Diagnostics;
using System.Text;

namespace Ltb.Driver;

public sealed class SystemSteamVrHostEnvironment : ISteamVrHostEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public string? GetLocalApplicationDataPath()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}

public sealed class SystemSteamVrFileSystem : ISteamVrFileSystem
{
    private readonly Action<string>? _afterOwnershipCheck;
    private readonly Action<string>? _afterStaging;

    public SystemSteamVrFileSystem()
    {
    }

    internal SystemSteamVrFileSystem(
        Action<string> afterOwnershipCheck,
        Action<string>? afterStaging = null)
    {
        _afterOwnershipCheck = afterOwnershipCheck ??
            throw new ArgumentNullException(nameof(afterOwnershipCheck));
        _afterStaging = afterStaging;
    }

    public bool FileExists(string path) => File.Exists(path);

    public string GetCanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken) =>
        new(File.ReadAllTextAsync(path, cancellationToken));

    /// <summary>
    /// Replaces the file at <paramref name="path"/> with
    /// <paramref name="replacementText"/> only when its current content equals
    /// <paramref name="expectedText"/>. The replacement is staged in a
    /// temporary file in the same directory, flushed to disk, verified, and
    /// committed with an atomic rename, so a crash or power loss at any point
    /// leaves the target as either the complete old content or the complete
    /// new content, never empty or partially written.
    /// </summary>
    /// <remarks>
    /// The exclusive handle used for the content comparison must be released
    /// before the rename can replace the target, so a small window remains
    /// between release and commit in which another process could alter the
    /// target; a write landing in that window is overwritten by the rename.
    /// Which complete content survives a crash immediately after the commit
    /// depends on filesystem journaling, because the directory entry is not
    /// explicitly synced.
    /// </remarks>
    public async ValueTask<bool> TryReplaceTextAtomicallyAsync(
        string path,
        string expectedText,
        string replacementText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedText);
        ArgumentNullException.ThrowIfNull(replacementText);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException(
                "File path must have a parent directory.", nameof(path));

        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
        }
        catch (IOException)
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var ownershipStream = stream)
            {
                string current;
                using (var reader = new StreamReader(
                    ownershipStream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: true))
                {
                    current = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!string.Equals(current, expectedText, StringComparison.Ordinal))
                {
                    return false;
                }

                _afterOwnershipCheck?.Invoke(path);
                var replacementBytes = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false).GetBytes(replacementText);
                await StageVerifiedReplacementAsync(
                    temporaryPath,
                    replacementBytes,
                    cancellationToken).ConfigureAwait(false);
                _afterStaging?.Invoke(temporaryPath);
            }

            // The ownership handle must be released before the rename can
            // replace the target on Windows; see the remarks for the window
            // this opens.
            File.Move(temporaryPath, fullPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async ValueTask StageVerifiedReplacementAsync(
        string temporaryPath,
        ReadOnlyMemory<byte> replacementBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(replacementBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        var writtenBytes = new byte[replacementBytes.Length];
        stream.Position = 0;
        await stream.ReadExactlyAsync(writtenBytes, cancellationToken).ConfigureAwait(false);
        if (!writtenBytes.AsSpan().SequenceEqual(replacementBytes.Span))
        {
            throw new IOException(
                $"Staged replacement verification failed for '{temporaryPath}'.");
        }
    }
}

public sealed class SystemSteamVrProcessRunner : ISteamVrProcessRunner
{
    public async ValueTask<SteamVrProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "SteamVR driver registration can execute only on Windows.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new IOException(
            $"Could not start SteamVR registration tool '{executable}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new SteamVrProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }
}
