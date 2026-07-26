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

    ValueTask<SteamVrDriverStartupInspection> InspectStartupAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<SteamVrDriverStartupInspection>(
            new NotSupportedException(
                "This lifecycle implementation does not provide next-start inspection."));

    ValueTask<SteamVrDriverLifecycleResult> RegisterAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default);

    ValueTask<SteamVrDriverLifecycleResult> RemoveAsync(
        SteamVrDriverRegistrationReceipt receipt,
        CancellationToken cancellationToken = default);

    async ValueTask<SteamVrDriverCleanupResult> RemoveOwnedAsync(
        IReadOnlyList<SteamVrDriverRegistrationReceipt> receipts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count != 1)
        {
            throw new NotSupportedException(
                "This lifecycle implementation does not provide transactional " +
                "multi-root driver cleanup.");
        }

        var result = await RemoveAsync(receipts[0], cancellationToken).ConfigureAwait(false);
        return new SteamVrDriverCleanupResult(
            result.Changed,
            result.RestartRequired,
            result.Readiness,
            result.Diagnostic,
            result.Paths,
            [result.Receipt.CanonicalDriverRoot]);
    }
}
