using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class SystemSteamVrFileSystemTests
{
    [Fact]
    public async Task OwnershipCheckAndCommitExcludeConcurrentWriterAtRealFileBoundary()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        await File.WriteAllTextAsync(path, "expected");
        var concurrentWriterWasBlocked = false;
        var fileSystem = new SystemSteamVrFileSystem(ownedPath =>
        {
            var failure = Record.Exception(() => File.WriteAllText(ownedPath, "concurrent"));
            concurrentWriterWasBlocked = failure is IOException;
        });

        try
        {
            var replaced = await fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                "expected",
                "replacement",
                CancellationToken.None);

            Assert.True(replaced);
            Assert.True(concurrentWriterWasBlocked);
            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceStagesFlushedTemporaryFileBeforeReleasingOwnership()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        await File.WriteAllTextAsync(path, "expected");
        string? stagedContent = null;
        string? stagedDirectory = null;
        var targetStayedOwned = false;
        var fileSystem = new SystemSteamVrFileSystem(
            _ => { },
            stagedPath =>
            {
                stagedContent = File.ReadAllText(stagedPath);
                stagedDirectory = Path.GetDirectoryName(stagedPath);
                var failure = Record.Exception(() => File.WriteAllText(path, "concurrent"));
                targetStayedOwned = failure is IOException;
            });

        try
        {
            var replaced = await fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                "expected",
                "replacement",
                CancellationToken.None);

            Assert.True(replaced);
            Assert.Equal("replacement", stagedContent);
            Assert.Equal(temporaryDirectory, stagedDirectory);
            Assert.True(targetStayedOwned);
            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FailureAfterStagingLeavesTargetUntouchedAndRemovesTemporaryFile()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        await File.WriteAllTextAsync(path, "expected");
        var fileSystem = new SystemSteamVrFileSystem(
            _ => { },
            _ => throw new InvalidOperationException("simulated pre-commit failure"));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fileSystem.TryReplaceTextAtomicallyAsync(
                    path,
                    "expected",
                    "replacement",
                    CancellationToken.None).AsTask());

            Assert.Equal("simulated pre-commit failure", exception.Message);
            Assert.Equal("expected", await File.ReadAllTextAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MismatchedExpectedTextReturnsFalseWithoutStaging()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        await File.WriteAllTextAsync(path, "diverged");
        var stagingWasObserved = false;
        var fileSystem = new SystemSteamVrFileSystem(
            _ => { },
            _ => stagingWasObserved = true);

        try
        {
            var replaced = await fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                "expected",
                "replacement",
                CancellationToken.None);

            Assert.False(replaced);
            Assert.False(stagingWasObserved);
            Assert.Equal("diverged", await File.ReadAllTextAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingTargetFileReturnsFalseWithoutStaging()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        var fileSystem = new SystemSteamVrFileSystem();

        try
        {
            var replaced = await fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                "expected",
                "replacement",
                CancellationToken.None);

            Assert.False(replaced);
            Assert.Empty(Directory.GetFiles(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ltb-system-file-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
