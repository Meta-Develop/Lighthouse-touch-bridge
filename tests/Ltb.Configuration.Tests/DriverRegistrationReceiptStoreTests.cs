using System.Collections.Concurrent;
using Ltb.Configuration;

namespace Ltb.Configuration.Tests;

public sealed class DriverRegistrationReceiptStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ltb-receipt-store-tests",
        Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_root, "driver", "registration-receipts.json");

    private string LockPath => Path.Combine(
        Path.GetDirectoryName(StorePath)!,
        $".{Path.GetFileName(StorePath)}.lock");

    [Fact]
    public void TryLoadReturnsNullForMissingFileOrDirectory()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Null(store.TryLoad(@"C:\ltb\driver_ltb"));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void SaveThenLoadRoundTripsEveryReceiptField()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var record = new DriverRegistrationReceiptRecord(
            @"C:\ltb\driver_ltb",
            DriverRegistrationReceiptSchema.PriorStateDisabled,
            ActivateMultipleDriversChanged: true,
            SteamVrSectionWasPresent: false,
            Guid.NewGuid());

        store.Save(record);
        var reloaded = new DriverRegistrationReceiptStore(StorePath)
            .TryLoad(@"C:\ltb\driver_ltb");

        Assert.Equal(record, reloaded);
    }

    [Fact]
    public void SameRootDifferentGenerationFailsClosedCaseInsensitively()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var original = Record(@"C:\LTB\Driver_LTB", Guid.NewGuid());
        var replacement = Record(@"c:\ltb\driver_ltb", Guid.NewGuid());

        store.Save(original);
        var failure = Assert.Throws<InvalidOperationException>(
            () => store.Save(replacement));

        Assert.Contains(
            "different authority generation",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(original, store.TryLoad(@"C:\LTB\DRIVER_LTB"));
    }

    [Fact]
    public void DeleteRemovesOnlyTheMatchingRoot()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var ltb = Record(@"C:\ltb\driver_ltb", Guid.NewGuid());
        var unrelated = Record(@"C:\drivers\unrelated", Guid.NewGuid());
        store.Save(ltb);
        store.Save(unrelated);

        store.Delete(@"C:\ltb\driver_ltb");

        Assert.Null(store.TryLoad(@"C:\ltb\driver_ltb"));
        Assert.Equal(unrelated, store.TryLoad(@"C:\drivers\unrelated"));
    }

    [Fact]
    public void LoadAllReturnsEveryReceiptInDeterministicRootOrder()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var second = Record(@"C:\ltb\second", Guid.NewGuid());
        var first = Record(@"C:\ltb\first", Guid.NewGuid());
        store.Save(second);
        store.Save(first);

        Assert.Equal([first, second], store.LoadAll());
    }

    [Fact]
    public void ExactSameRootRecordIsAnIdempotentSave()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var record = Record(@"C:\ltb\driver_ltb", Guid.NewGuid());

        store.Save(record);
        var originalText = File.ReadAllText(StorePath);
        store.Save(record);

        Assert.Equal(originalText, File.ReadAllText(StorePath));
        Assert.Equal([record], store.LoadAll());
    }

    [Fact]
    public async Task ConcurrentDistinctSavesRetainEveryReceipt()
    {
        var records = Enumerable.Range(0, 24)
            .Select(index => Record(
                $@"C:\ltb\driver-{index:D2}",
                Guid.NewGuid()))
            .ToArray();
        using var start = new ManualResetEventSlim(initialState: false);
        var tasks = records
            .Select(record => Task.Run(() =>
            {
                start.Wait();
                new DriverRegistrationReceiptStore(StorePath).Save(record);
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(
            records.OrderBy(
                record => record.CanonicalDriverRoot,
                StringComparer.OrdinalIgnoreCase),
            new DriverRegistrationReceiptStore(StorePath).LoadAll());
    }

    [Fact]
    public async Task ConcurrentDistinctConditionalDeletesRetainEveryOtherReceipt()
    {
        var records = Enumerable.Range(0, 18)
            .Select(index => Record(
                $@"C:\ltb\driver-{index:D2}",
                Guid.NewGuid()))
            .ToArray();
        var store = new DriverRegistrationReceiptStore(StorePath);
        store.SaveAll(records);
        var deleted = records.Where((_, index) => index % 2 == 0).ToArray();
        var retained = records.Except(deleted).ToArray();
        using var start = new ManualResetEventSlim(initialState: false);
        var tasks = deleted
            .Select(record => Task.Run(() =>
            {
                start.Wait();
                Assert.True(
                    new DriverRegistrationReceiptStore(StorePath).Delete(record));
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(retained, store.LoadAll());
    }

    [Fact]
    public async Task ConcurrentDistinctSaveAndConditionalDeleteDoNotLoseEitherUpdate()
    {
        var deleted = Record(@"C:\ltb\delete", Guid.NewGuid());
        var retained = Record(@"C:\ltb\retain", Guid.NewGuid());
        var added = Record(@"C:\ltb\add", Guid.NewGuid());
        var store = new DriverRegistrationReceiptStore(StorePath);
        store.SaveAll([deleted, retained]);
        using var start = new ManualResetEventSlim(initialState: false);
        var save = Task.Run(() =>
        {
            start.Wait();
            new DriverRegistrationReceiptStore(StorePath).Save(added);
        });
        var delete = Task.Run(() =>
        {
            start.Wait();
            Assert.True(
                new DriverRegistrationReceiptStore(StorePath).Delete(deleted));
        });

        start.Set();
        await Task.WhenAll(save, delete);

        Assert.Equal([added, retained], store.LoadAll());
    }

    [Fact]
    public async Task ConcurrentSameRootDifferentSavesYieldOneWinnerAndOneConflict()
    {
        var first = Record(@"C:\ltb\driver_ltb", Guid.NewGuid());
        var second = Record(@"c:\LTB\driver_ltb", Guid.NewGuid());
        using var start = new ManualResetEventSlim(initialState: false);
        var outcomes = new ConcurrentBag<Exception?>();
        var tasks = new[] { first, second }
            .Select(record => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    new DriverRegistrationReceiptStore(StorePath).Save(record);
                    outcomes.Add(null);
                }
                catch (Exception exception)
                {
                    outcomes.Add(exception);
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is InvalidOperationException);
        Assert.Contains(
            new DriverRegistrationReceiptStore(StorePath).LoadAll().Single(),
            new[] { first, second });
    }

    [Fact]
    public void ConditionalDeleteRefusesAReplacementGenerationWithoutMutation()
    {
        var current = Record(@"C:\ltb\driver_ltb", Guid.NewGuid());
        var stale = current with { OwnershipToken = Guid.NewGuid() };
        var store = new DriverRegistrationReceiptStore(StorePath);
        store.Save(current);
        var originalText = File.ReadAllText(StorePath);

        var failure = Assert.Throws<InvalidOperationException>(
            () => store.Delete(stale));

        Assert.Contains(
            "conditional deletion was refused",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(originalText, File.ReadAllText(StorePath));
        Assert.Equal(current, store.TryLoad(current.CanonicalDriverRoot));
    }

    [Fact]
    public void ConditionalDeleteBatchRefusesAllMutationWhenOneGenerationMismatches()
    {
        var first = Record(@"C:\ltb\first", Guid.NewGuid());
        var second = Record(@"C:\ltb\second", Guid.NewGuid());
        var staleSecond = second with { OwnershipToken = Guid.NewGuid() };
        var store = new DriverRegistrationReceiptStore(StorePath);
        store.SaveAll([first, second]);

        Assert.Throws<InvalidOperationException>(
            () => store.DeleteAll(
                new DriverRegistrationReceiptRecord[] { first, staleSecond }));

        Assert.Equal([first, second], store.LoadAll());
    }

    [Fact]
    public void ExclusiveLockWaitIsBoundedAndLeavesTheStoreUntouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        using var heldLock = new FileStream(
            LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var store = new DriverRegistrationReceiptStore(
            StorePath,
            lockTimeout: TimeSpan.FromMilliseconds(120),
            lockRetryDelay: TimeSpan.FromMilliseconds(10));

        var failure = Assert.Throws<TimeoutException>(
            () => store.Save(Record(@"C:\ltb\driver_ltb", Guid.NewGuid())));

        Assert.Contains("receipt lock", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void ExclusiveLockWaitObservesCancellation()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        using var heldLock = new FileStream(
            LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var store = new DriverRegistrationReceiptStore(
            StorePath,
            lockTimeout: TimeSpan.FromSeconds(5),
            lockRetryDelay: TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(80));

        Assert.ThrowsAny<OperationCanceledException>(
            () => store.Save(
                Record(@"C:\ltb\driver_ltb", Guid.NewGuid()),
                cancellation.Token));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void AtomicUpdatesLeaveOnlyThePersistentLockAndNoTemporaryResidue()
    {
        var first = Record(@"C:\ltb\first", Guid.NewGuid());
        var second = Record(@"C:\ltb\second", Guid.NewGuid());
        var store = new DriverRegistrationReceiptStore(StorePath);

        store.SaveAll([second, first]);
        Assert.True(store.Delete(first));

        Assert.True(File.Exists(LockPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(StorePath)!)
            .Where(path => path.EndsWith(".tmp", StringComparison.Ordinal)));
        Assert.Equal([second], store.LoadAll());
    }

    [Fact]
    public void DeleteOfAnUnknownRootLeavesTheStoreUntouched()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);

        store.Delete(@"C:\ltb\driver_ltb");

        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void MalformedJsonFailsLoudlyInsteadOfGrantingOrDroppingOwnership()
    {
        WriteStoreFile("{ not json");
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Throws<InvalidDataException>(() => store.TryLoad(@"C:\ltb\driver_ltb"));
    }

    [Fact]
    public void MalformedStoreRefusesMutationWithoutTemporaryResidue()
    {
        const string malformed = "{ not json";
        WriteStoreFile(malformed);
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Throws<InvalidDataException>(
            () => store.Save(Record(@"C:\ltb\driver_ltb", Guid.NewGuid())));

        Assert.Equal(malformed, File.ReadAllText(StorePath));
        Assert.True(File.Exists(LockPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(StorePath)!)
            .Where(path => path.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        WriteStoreFile("""{ "schema_version": 2, "receipts": [] }""");
        var store = new DriverRegistrationReceiptStore(StorePath);

        var failure = Assert.Throws<InvalidDataException>(
            () => store.TryLoad(@"C:\ltb\driver_ltb"));

        Assert.Contains("schema_version", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPriorSettingStateIsRejected()
    {
        WriteStoreFile($$"""
            {
              "schema_version": 1,
              "receipts": [
                {
                  "canonical_driver_root": "C:\\ltb\\driver_ltb",
                  "prior_activate_multiple_drivers": "sometimes",
                  "activate_multiple_drivers_changed": true,
                  "steamvr_section_was_present": true,
                  "ownership_token": "{{Guid.NewGuid()}}"
                }
              ]
            }
            """);
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Throws<InvalidDataException>(() => store.TryLoad(@"C:\ltb\driver_ltb"));
    }

    [Fact]
    public void DuplicateCanonicalRootsAreRejected()
    {
        var token = Guid.NewGuid();
        WriteStoreFile($$"""
            {
              "schema_version": 1,
              "receipts": [
                {
                  "canonical_driver_root": "C:\\ltb\\driver_ltb",
                  "prior_activate_multiple_drivers": "disabled",
                  "activate_multiple_drivers_changed": true,
                  "steamvr_section_was_present": true,
                  "ownership_token": "{{token}}"
                },
                {
                  "canonical_driver_root": "c:\\LTB\\driver_ltb",
                  "prior_activate_multiple_drivers": "enabled",
                  "activate_multiple_drivers_changed": false,
                  "steamvr_section_was_present": true,
                  "ownership_token": "{{token}}"
                }
              ]
            }
            """);
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Throws<InvalidDataException>(() => store.TryLoad(@"C:\ltb\driver_ltb"));
    }

    [Fact]
    public void RecordConstructionRejectsUnknownPriorState()
    {
        Assert.Throws<ArgumentException>(() => Record(
            @"C:\ltb\driver_ltb",
            Guid.NewGuid(),
            priorState: "unknown"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DriverRegistrationReceiptRecord Record(
        string root,
        Guid token,
        string priorState = DriverRegistrationReceiptSchema.PriorStateDisabled) => new(
        root,
        priorState,
        ActivateMultipleDriversChanged: true,
        SteamVrSectionWasPresent: true,
        token);

    private void WriteStoreFile(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, contents);
    }
}
