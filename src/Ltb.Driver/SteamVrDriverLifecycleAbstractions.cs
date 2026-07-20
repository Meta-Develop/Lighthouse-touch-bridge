namespace Ltb.Driver;

public interface ISteamVrHostEnvironment
{
    bool IsWindows { get; }

    string? GetLocalApplicationDataPath();
}

public interface ISteamVrFileSystem
{
    bool FileExists(string path);

    string GetCanonicalPath(string path);

    ValueTask<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    ValueTask<bool> TryReplaceTextAtomicallyAsync(
        string path,
        string expectedText,
        string replacementText,
        CancellationToken cancellationToken);
}

public interface ISteamVrProcessRunner
{
    ValueTask<SteamVrProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public readonly record struct SteamVrProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface ISteamVrDriverLifecycle : IDisposable
{
    ValueTask<SteamVrPaths> DiscoverAsync(CancellationToken cancellationToken = default);

    ValueTask<SteamVrDriverInspection> InspectAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default);

    ValueTask<SteamVrDriverLifecycleResult> RegisterAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default);

    ValueTask<SteamVrDriverLifecycleResult> RemoveAsync(
        SteamVrDriverRegistrationReceipt receipt,
        CancellationToken cancellationToken = default);
}
