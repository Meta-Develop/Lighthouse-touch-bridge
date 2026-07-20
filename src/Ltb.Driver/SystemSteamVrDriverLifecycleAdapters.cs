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

    public SystemSteamVrFileSystem()
    {
    }

    internal SystemSteamVrFileSystem(Action<string> afterOwnershipCheck)
    {
        _afterOwnershipCheck = afterOwnershipCheck ??
            throw new ArgumentNullException(nameof(afterOwnershipCheck));
    }

    public bool FileExists(string path) => File.Exists(path);

    public string GetCanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken) =>
        new(File.ReadAllTextAsync(path, cancellationToken));

    public async ValueTask<bool> TryReplaceTextAtomicallyAsync(
        string path,
        string expectedText,
        string replacementText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedText);
        ArgumentNullException.ThrowIfNull(replacementText);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return false;
        }

        await using var ownershipStream = stream;
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
        var originalBytes = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false).GetBytes(current);
        var mutationStarted = false;
        try
        {
            mutationStarted = true;
            await WriteOwnedBytesAsync(
                ownershipStream,
                replacementBytes,
                cancellationToken).ConfigureAwait(false);
            var writtenBytes = new byte[replacementBytes.Length];
            ownershipStream.Position = 0;
            await ownershipStream.ReadExactlyAsync(
                writtenBytes,
                cancellationToken).ConfigureAwait(false);
            if (!writtenBytes.AsSpan().SequenceEqual(replacementBytes))
            {
                throw new IOException($"Ownership-checked update verification failed for '{path}'.");
            }

            return true;
        }
        catch
        {
            if (mutationStarted)
            {
                await WriteOwnedBytesAsync(
                    ownershipStream,
                    originalBytes,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async ValueTask WriteOwnedBytesAsync(
        FileStream ownershipStream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ownershipStream.Position = 0;
        ownershipStream.SetLength(0);
        await ownershipStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await ownershipStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        ownershipStream.Flush(flushToDisk: true);
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
