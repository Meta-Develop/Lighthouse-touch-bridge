using Ltb.Driver;

namespace Ltb.Driver.Tests;

/// <summary>
/// Restart-safe removal authority: a durable receipt store lets a fresh
/// lifecycle instance (a restarted application) remove LTB's own registration
/// and restore the recorded prior setting, while every foreign or forged
/// receipt remains refused.
/// </summary>
public sealed class SteamVrDriverLifecycleReceiptPersistenceTests
{
    [Fact]
    public async Task PersistedReceiptRemovesRegistrationAfterApplicationRestart()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        SteamVrDriverRegistrationReceipt issued;
        using (var firstProcessLifecycle = NewLifecycle(fixture, store))
        {
            var registration = await firstProcessLifecycle.RegisterAsync(
                fixture.StagedDriverRoot);
            issued = registration.Receipt;
        }

        Assert.Equal(issued, store.TryLoad(issued.CanonicalDriverRoot));

        using var restartedLifecycle = NewLifecycle(fixture, store);
        var persisted = store.TryLoad(issued.CanonicalDriverRoot);
        Assert.NotNull(persisted);
        var removal = await restartedLifecycle.RemoveAsync(persisted!);
        var repeatedRemoval = await restartedLifecycle.RemoveAsync(persisted!);

        Assert.True(removal.Changed);
        Assert.True(removal.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.RestartRequired, removal.Readiness);
        Assert.False(repeatedRemoval.Changed);
        Assert.Equal(SteamVrDriverReadiness.NotRegistered, repeatedRemoval.Readiness);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Null(store.TryLoad(issued.CanonicalDriverRoot));
    }

    [Fact]
    public async Task ReregistrationAfterRestartReusesPersistedPriorSettingSnapshot()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        SteamVrDriverRegistrationReceipt issued;
        using (var firstProcessLifecycle = NewLifecycle(fixture, store))
        {
            issued = (await firstProcessLifecycle.RegisterAsync(fixture.StagedDriverRoot))
                .Receipt;
        }

        using var restartedLifecycle = NewLifecycle(fixture, store);
        var repeatedRegistration = await restartedLifecycle.RegisterAsync(
            fixture.StagedDriverRoot);

        // Without persistence the second run would snapshot the already-enabled
        // setting and lose the fact that LTB originally changed it.
        Assert.False(repeatedRegistration.Changed);
        Assert.Equal(issued, repeatedRegistration.Receipt);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            repeatedRegistration.Receipt.PriorActivateMultipleDrivers);
        Assert.True(repeatedRegistration.Receipt.ActivateMultipleDriversChanged);

        await restartedLifecycle.RemoveAsync(repeatedRegistration.Receipt);

        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task RemovalWithoutPersistedAuthorityIsRefusedAfterRestart()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        SteamVrDriverRegistrationReceipt issued;
        using (var firstProcessLifecycle = NewLifecycle(fixture, store))
        {
            issued = (await firstProcessLifecycle.RegisterAsync(fixture.StagedDriverRoot))
                .Receipt;
        }

        using var lifecycleWithEmptyStore = NewLifecycle(fixture, new MemoryReceiptStore());
        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => lifecycleWithEmptyStore.RemoveAsync(issued).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Contains(fixture.StagedDriverRoot, fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task ForgedTokenIsRefusedAgainstPersistedReceiptAfterRestart()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        SteamVrDriverRegistrationReceipt issued;
        using (var firstProcessLifecycle = NewLifecycle(fixture, store))
        {
            issued = (await firstProcessLifecycle.RegisterAsync(fixture.StagedDriverRoot))
                .Receipt;
        }

        using var restartedLifecycle = NewLifecycle(fixture, store);
        var forgedReceipt = issued with { OwnershipToken = Guid.NewGuid() };
        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => restartedLifecycle.RemoveAsync(forgedReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Contains(fixture.StagedDriverRoot, fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RemovalRefusesReceiptForForeignDriverRootAfterRestart()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        using (var firstProcessLifecycle = NewLifecycle(fixture, store))
        {
            _ = await firstProcessLifecycle.RegisterAsync(fixture.StagedDriverRoot);
        }

        using var restartedLifecycle = NewLifecycle(fixture, store);
        var foreignReceipt = new SteamVrDriverRegistrationReceipt(
            fixture.OtherDriverRoot,
            SteamVrActivateMultipleDriversState.Disabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => restartedLifecycle.RemoveAsync(foreignReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
        Assert.Empty(fixture.ProcessRunner.Calls
            .Where(call => call.Verb == "removedriver"));
    }

    [Fact]
    public async Task CorruptPersistedRootCannotGrantRemovalOfAForeignPath()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var store = new MemoryReceiptStore();
        // A tampered store entry keyed by the LTB root but naming the foreign
        // root must be ignored by the canonical-root cross-check.
        var tamperedReceipt = new SteamVrDriverRegistrationReceipt(
            fixture.OtherDriverRoot,
            SteamVrActivateMultipleDriversState.Disabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.SaveUnderRoot(fixture.StagedDriverRoot, tamperedReceipt);

        using var lifecycle = NewLifecycle(fixture, store);
        var claimedReceipt = tamperedReceipt with { CanonicalDriverRoot = fixture.StagedDriverRoot };
        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => lifecycle.RemoveAsync(claimedReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    private static SteamVrDriverLifecycle NewLifecycle(
        SteamVrLifecycleFixture fixture,
        ISteamVrDriverReceiptStore store) =>
        new(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = fixture.LocalApplicationData,
            },
            fixture.FileSystem,
            fixture.ProcessRunner,
            store);

    private sealed class MemoryReceiptStore : ISteamVrDriverReceiptStore
    {
        private readonly Dictionary<string, SteamVrDriverRegistrationReceipt> _receipts =
            new(StringComparer.OrdinalIgnoreCase);

        public SteamVrDriverRegistrationReceipt? TryLoad(string canonicalDriverRoot) =>
            _receipts.TryGetValue(canonicalDriverRoot, out var receipt) ? receipt : null;

        public void Save(SteamVrDriverRegistrationReceipt receipt) =>
            _receipts[receipt.CanonicalDriverRoot] = receipt;

        public void Delete(string canonicalDriverRoot) =>
            _receipts.Remove(canonicalDriverRoot);

        public void SaveUnderRoot(string root, SteamVrDriverRegistrationReceipt receipt) =>
            _receipts[root] = receipt;
    }
}
