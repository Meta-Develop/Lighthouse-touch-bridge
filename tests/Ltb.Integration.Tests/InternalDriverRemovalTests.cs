using Ltb.App;
using Ltb.Driver;

namespace Ltb.Integration.Tests;

/// <summary>
/// Product-surface coverage for restart-safe first-party driver removal: the
/// zero-input removal orchestrator and the remove-driver CLI command.
/// </summary>
public sealed class InternalDriverRemovalTests
{
    private const string StagedDriverRoot = @"C:\ltb\app\driver_ltb";

    [Fact]
    public async Task RemovalUsesThePersistedReceiptAsRemovalAuthority()
    {
        var receipt = Receipt();
        var store = new MemoryReceiptStore();
        store.Save(receipt);
        var lifecycle = new FakeLifecycle(StartupInspection(
            SteamVrDriverStartupState.ReceiptOwnedRegistration,
            canonicalLtbDriverRoots: [StagedDriverRoot],
            receipts: [receipt],
            matchingReceipt: receipt,
            canRemoveAutomatically: true));
        await using var removal = new InternalDriverRemoval(lifecycle, store, StagedDriverRoot);

        var result = await removal.RemoveAsync();

        Assert.Equal([StagedDriverRoot], lifecycle.InspectedRoots);
        Assert.Equal([receipt], lifecycle.RemovedReceipts);
        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
        Assert.Equal("removal diagnostic", result.Diagnostic);
    }

    [Fact]
    public async Task ReceiptlessRegisteredDriverIsAdoptedWithoutClaimingTheSetting()
    {
        var store = new MemoryReceiptStore();
        var lifecycle = new FakeLifecycle(StartupInspection(
            SteamVrDriverStartupState.ReceiptlessArtifactProvenRegistration,
            setting: SteamVrActivateMultipleDriversState.Enabled));
        await using var removal = new InternalDriverRemoval(lifecycle, store, StagedDriverRoot);

        _ = await removal.RemoveAsync();

        var adopted = Assert.Single(lifecycle.RemovedReceipts);
        Assert.Equal(StagedDriverRoot, adopted.CanonicalDriverRoot);
        Assert.False(adopted.ActivateMultipleDriversChanged);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            adopted.PriorActivateMultipleDrivers);
        Assert.Equal(adopted, store.TryLoad(StagedDriverRoot));
    }

    [Fact]
    public async Task NothingIsRemovedWithoutReceiptOrRegistration()
    {
        var store = new MemoryReceiptStore();
        var lifecycle = new FakeLifecycle(StartupInspection(
            SteamVrDriverStartupState.NoLtbRegistration));
        await using var removal = new InternalDriverRemoval(lifecycle, store, StagedDriverRoot);

        var result = await removal.RemoveAsync();

        Assert.Empty(lifecycle.RemovedReceipts);
        Assert.False(result.Changed);
        Assert.False(result.RestartRequired);
        Assert.Contains("nothing to remove", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistedReceiptStillDrivesSettingsOnlyRestorationWhenUnregistered()
    {
        var receipt = Receipt();
        var store = new MemoryReceiptStore();
        store.Save(receipt);
        var lifecycle = new FakeLifecycle(StartupInspection(
            SteamVrDriverStartupState.ReceiptOnlyNoRegistration,
            receipts: [receipt],
            canRemoveAutomatically: true));
        await using var removal = new InternalDriverRemoval(lifecycle, store, StagedDriverRoot);

        _ = await removal.RemoveAsync();

        Assert.Equal([receipt], lifecycle.RemovedReceipts);
    }

    [Fact]
    public async Task ProvenDuplicateRootsAreCleanedInOneAuthorizedBatch()
    {
        var receipt = Receipt();
        var relocatedRoot = @"C:\ltb\old-package\driver_ltb";
        var store = new MemoryReceiptStore();
        store.Save(receipt);
        var lifecycle = new FakeLifecycle(StartupInspection(
            SteamVrDriverStartupState.DuplicateRegistrations,
            canonicalLtbDriverRoots: [StagedDriverRoot, relocatedRoot],
            receipts: [receipt],
            canRemoveAutomatically: true));
        await using var removal = new InternalDriverRemoval(
            lifecycle,
            store,
            StagedDriverRoot);

        var result = await removal.RemoveAsync();

        var batch = Assert.Single(lifecycle.RemovedBatches);
        Assert.Equal(2, batch.Count);
        Assert.Equal(receipt, batch[0]);
        var adopted = batch[1];
        Assert.Equal(relocatedRoot, adopted.CanonicalDriverRoot);
        Assert.False(adopted.ActivateMultipleDriversChanged);
        Assert.Equal(adopted, store.TryLoad(relocatedRoot));
        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
    }

    [Theory]
    [InlineData(SteamVrDriverStartupState.StaleReceiptRegistrationMismatch)]
    [InlineData(SteamVrDriverStartupState.DuplicateRegistrations)]
    [InlineData(SteamVrDriverStartupState.AmbiguousNonCanonicalRegistration)]
    public async Task AmbiguousStartupStateRefusesExitRemovalWithoutMutation(
        SteamVrDriverStartupState state)
    {
        var store = new MemoryReceiptStore();
        var lifecycle = new FakeLifecycle(StartupInspection(
            state,
            canonicalLtbDriverRoots: [StagedDriverRoot],
            canRemoveAutomatically: false));
        await using var removal = new InternalDriverRemoval(
            lifecycle,
            store,
            StagedDriverRoot);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => removal.RemoveAsync().AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Empty(lifecycle.RemovedReceipts);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public async Task NextStartInspectionIsExplicitAndReadOnly()
    {
        var expected = StartupInspection(
            SteamVrDriverStartupState.NoLtbRegistration,
            unrelatedExternalDriverRoots:
            [
                @"C:\drivers\01spacecalibrator",
                @"C:\drivers\bigscreenbeyond",
                @"C:\drivers\vmt",
                @"C:\drivers\alvr_server",
            ]);
        var lifecycle = new FakeLifecycle(expected);
        var store = new MemoryReceiptStore();
        await using var maintenance = new InternalDriverRemoval(
            lifecycle,
            store,
            StagedDriverRoot);

        var actual = await maintenance.InspectNextStartAsync();

        Assert.Equal(expected, actual);
        Assert.Empty(lifecycle.RemovedReceipts);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void RemoveDriverCommandParsesWithoutOptions()
    {
        Assert.True(AppCommandLineOptions.TryParse(
            ["remove-driver"],
            out var options,
            out var error), error);
        Assert.Equal(AppCommand.RemoveDriver, options.Command);
        Assert.Null(options.UnsupportedMigrationCommand);
    }

    [Theory]
    [InlineData("--steamvr-settings", "steamvr.vrsettings")]
    [InlineData("--profiles", "profiles.json")]
    [InlineData("--vmt-slot", "1")]
    public void RemoveDriverCommandRejectsOptions(string option, string value)
    {
        Assert.False(AppCommandLineOptions.TryParse(
            ["remove-driver", option, value],
            out _,
            out var error));
        Assert.Contains("accepts no options", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDriverCommandRunsInjectedRemoverAndReportsOutcome()
    {
        var remover = new ScriptedRemover(new InternalDriverRemovalResult(
            Changed: true,
            RestartRequired: true,
            "driver_ltb was removed without changing unrelated drivers; restart SteamVR."));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["remove-driver"],
            _ => throw new InvalidOperationException(
                "remove-driver must not create an internal-driver session."),
            output,
            error,
            CancellationToken.None,
            () => remover);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, remover.RemoveCount);
        Assert.True(remover.Disposed);
        Assert.Contains(
            "Remove First-Party Driver Registration",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "changed=true restart_required=true",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("restart SteamVR", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, 2)]
    [InlineData(SteamVrDriverDiagnosticCode.RollbackFailed, 4)]
    public async Task RemoveDriverCommandMapsLifecycleFailuresToExitCodes(
        SteamVrDriverDiagnosticCode diagnosticCode,
        int expectedExitCode)
    {
        var remover = new ScriptedRemover(new SteamVrDriverLifecycleException(
            diagnosticCode,
            "scripted removal failure"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["remove-driver"],
            _ => throw new InvalidOperationException(
                "remove-driver must not create an internal-driver session."),
            output,
            error,
            CancellationToken.None,
            () => remover);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.True(remover.Disposed);
        Assert.Contains("scripted removal failure", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            diagnosticCode.ToString(),
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SteamVrActivateMultipleDriversState.Absent)]
    [InlineData(SteamVrActivateMultipleDriversState.Disabled)]
    [InlineData(SteamVrActivateMultipleDriversState.Enabled)]
    public void ConfigurationReceiptStoreAdapterRoundTripsEveryPriorSettingState(
        SteamVrActivateMultipleDriversState priorState)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ltb-receipt-adapter-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new ConfigurationSteamVrDriverReceiptStore(
                Path.Combine(root, "registration-receipts.json"));
            var receipt = Receipt() with { PriorActivateMultipleDrivers = priorState };

            adapter.Save(receipt);
            var reloaded = adapter.TryLoad(StagedDriverRoot);
            var all = adapter.LoadAll();
            adapter.Delete(StagedDriverRoot);

            Assert.Equal(receipt, reloaded);
            Assert.Equal([receipt], all);
            Assert.Null(adapter.TryLoad(StagedDriverRoot));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ConfigurationReceiptStoreAdapterSavesAndDeletesBatchAtomically()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ltb-receipt-adapter-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new ConfigurationSteamVrDriverReceiptStore(
                Path.Combine(root, "registration-receipts.json"));
            var first = Receipt();
            var second = first with
            {
                CanonicalDriverRoot = @"C:\ltb\old-package\driver_ltb",
                ActivateMultipleDriversChanged = false,
                PriorActivateMultipleDrivers =
                    SteamVrActivateMultipleDriversState.Enabled,
                OwnershipToken = Guid.NewGuid(),
            };

            adapter.SaveAll([first, second]);
            Assert.Equal([first, second], adapter.LoadAll());

            adapter.DeleteAll(
                [first.CanonicalDriverRoot, second.CanonicalDriverRoot]);
            Assert.Empty(adapter.LoadAll());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SteamVrDriverRegistrationReceipt Receipt() => new(
        StagedDriverRoot,
        SteamVrActivateMultipleDriversState.Disabled,
        ActivateMultipleDriversChanged: true,
        SteamVrSectionWasPresent: true,
        Guid.NewGuid());

    private static SteamVrDriverStartupInspection StartupInspection(
        SteamVrDriverStartupState state,
        SteamVrActivateMultipleDriversState setting =
            SteamVrActivateMultipleDriversState.Enabled,
        IReadOnlyList<string>? canonicalLtbDriverRoots = null,
        IReadOnlyList<string>? unrelatedExternalDriverRoots = null,
        IReadOnlyList<SteamVrDriverRegistrationReceipt>? receipts = null,
        SteamVrDriverRegistrationReceipt? matchingReceipt = null,
        bool? canRemoveAutomatically = null) => new(
        new SteamVrPaths(
            @"C:\Users\user\AppData\Local\openvr\openvrpaths.vrpath",
            @"C:\Steam\steamapps\common\SteamVR",
            @"C:\Steam\config",
            @"C:\Steam\steamapps\common\SteamVR\bin\win64\vrpathreg.exe",
            @"C:\Steam\config\steamvr.vrsettings"),
        StagedDriverRoot,
        "driver_ltb-0.1.0-ipc-1.0",
        state,
        setting,
        canonicalLtbDriverRoots ??
            (state == SteamVrDriverStartupState.ReceiptlessArtifactProvenRegistration
                ? [StagedDriverRoot]
                : []),
        unrelatedExternalDriverRoots ?? [],
        receipts ?? [],
        matchingReceipt,
        canRemoveAutomatically ??
            state is SteamVrDriverStartupState.ReceiptOwnedRegistration
                or SteamVrDriverStartupState.ReceiptOnlyNoRegistration
                or SteamVrDriverStartupState.ReceiptlessArtifactProvenRegistration,
        "startup diagnostic");

    private sealed class MemoryReceiptStore : ISteamVrDriverReceiptStore
    {
        private readonly Dictionary<string, SteamVrDriverRegistrationReceipt> _receipts =
            new(StringComparer.OrdinalIgnoreCase);

        public SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot) =>
            _receipts.TryGetValue(canonicalDriverRoot, out var receipt) ? receipt : null;

        public IReadOnlyList<SteamVrDriverRegistrationReceipt> LoadAll() =>
            _receipts.Values.ToArray();

        public void Save(SteamVrDriverRegistrationReceipt receipt) =>
            _receipts[receipt.CanonicalDriverRoot] = receipt;

        public void Delete(string canonicalDriverRoot) =>
            _receipts.Remove(canonicalDriverRoot);
    }

    private sealed class FakeLifecycle : ISteamVrDriverLifecycle
    {
        private readonly SteamVrDriverStartupInspection _inspection;

        public FakeLifecycle(SteamVrDriverStartupInspection inspection)
        {
            _inspection = inspection;
        }

        public List<string> InspectedRoots { get; } = [];

        public List<SteamVrDriverRegistrationReceipt> RemovedReceipts { get; } = [];

        public List<IReadOnlyList<SteamVrDriverRegistrationReceipt>> RemovedBatches { get; } = [];

        public ValueTask<SteamVrPaths> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_inspection.Paths);

        public ValueTask<SteamVrDriverInspection> InspectAsync(
            string stagedDriverRoot,
            CancellationToken cancellationToken = default)
        {
            InspectedRoots.Add(stagedDriverRoot);
            return ValueTask.FromResult(new SteamVrDriverInspection(
                _inspection.Paths,
                _inspection.CanonicalStagedDriverRoot,
                _inspection.StagedBuildId,
                _inspection.CanonicalLtbDriverRoots.Contains(
                    _inspection.CanonicalStagedDriverRoot,
                    StringComparer.OrdinalIgnoreCase),
                _inspection.ActivateMultipleDrivers));
        }

        public ValueTask<SteamVrDriverStartupInspection> InspectStartupAsync(
            string stagedDriverRoot,
            CancellationToken cancellationToken = default)
        {
            InspectedRoots.Add(stagedDriverRoot);
            return ValueTask.FromResult(_inspection);
        }

        public ValueTask<SteamVrDriverLifecycleResult> RegisterAsync(
            string stagedDriverRoot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Removal must never register the driver.");

        public ValueTask<SteamVrDriverLifecycleResult> RemoveAsync(
            SteamVrDriverRegistrationReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            RemovedReceipts.Add(receipt);
            return ValueTask.FromResult(new SteamVrDriverLifecycleResult(
                Changed: true,
                RestartRequired: true,
                SteamVrDriverReadiness.RestartRequired,
                "removal diagnostic",
                _inspection.Paths,
                receipt));
        }

        public ValueTask<SteamVrDriverCleanupResult> RemoveOwnedAsync(
            IReadOnlyList<SteamVrDriverRegistrationReceipt> receipts,
            CancellationToken cancellationToken = default)
        {
            RemovedBatches.Add(receipts.ToArray());
            RemovedReceipts.AddRange(receipts);
            return ValueTask.FromResult(new SteamVrDriverCleanupResult(
                Changed: true,
                RestartRequired: true,
                SteamVrDriverReadiness.RestartRequired,
                "removal diagnostic",
                _inspection.Paths,
                receipts.Select(receipt => receipt.CanonicalDriverRoot).ToArray()));
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedRemover : IInternalDriverRemover
    {
        private readonly InternalDriverRemovalResult? _result;
        private readonly Exception? _failure;

        public ScriptedRemover(InternalDriverRemovalResult result)
        {
            _result = result;
        }

        public ScriptedRemover(Exception failure)
        {
            _failure = failure;
        }

        public int RemoveCount { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<InternalDriverRemovalResult> RemoveAsync(
            CancellationToken cancellationToken = default)
        {
            RemoveCount++;
            return _failure is null
                ? ValueTask.FromResult(_result!)
                : ValueTask.FromException<InternalDriverRemovalResult>(_failure);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
