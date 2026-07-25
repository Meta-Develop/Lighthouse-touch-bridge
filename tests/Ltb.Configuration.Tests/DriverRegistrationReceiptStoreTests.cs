using Ltb.Configuration;

namespace Ltb.Configuration.Tests;

public sealed class DriverRegistrationReceiptStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ltb-receipt-store-tests",
        Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_root, "driver", "registration-receipts.json");

    [Fact]
    public void TryLoadReturnsNullForMissingFileOrDirectory()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);

        Assert.Null(store.TryLoad(@"C:\ltb\driver_ltb"));
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
    public void LookupAndOverwriteUseOrdinalIgnoreCaseRootKeys()
    {
        var store = new DriverRegistrationReceiptStore(StorePath);
        var original = Record(@"C:\LTB\Driver_LTB", Guid.NewGuid());
        var replacement = Record(@"c:\ltb\driver_ltb", Guid.NewGuid());

        store.Save(original);
        store.Save(replacement);

        Assert.Equal(replacement, store.TryLoad(@"C:\LTB\DRIVER_LTB"));
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
