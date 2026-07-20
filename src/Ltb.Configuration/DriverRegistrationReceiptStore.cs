using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ltb.Configuration;

/// <summary>Schema constants for the durable driver-registration receipt store.</summary>
public static class DriverRegistrationReceiptSchema
{
    public const int CurrentVersion = 1;

    public const string PriorStateAbsent = "absent";

    public const string PriorStateDisabled = "disabled";

    public const string PriorStateEnabled = "enabled";
}

/// <summary>
/// One durable registration receipt: the exact canonical driver root LTB
/// registered plus the pre-registration <c>activateMultipleDrivers</c>
/// snapshot needed to restore user configuration on removal.
/// </summary>
public sealed record DriverRegistrationReceiptRecord(
    string CanonicalDriverRoot,
    string PriorActivateMultipleDrivers,
    bool ActivateMultipleDriversChanged,
    bool SteamVrSectionWasPresent,
    Guid OwnershipToken)
{
    public string CanonicalDriverRoot { get; } =
        Require(CanonicalDriverRoot, nameof(CanonicalDriverRoot));

    public string PriorActivateMultipleDrivers { get; } =
        RequirePriorState(PriorActivateMultipleDrivers);

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static string RequirePriorState(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value is DriverRegistrationReceiptSchema.PriorStateAbsent
            or DriverRegistrationReceiptSchema.PriorStateDisabled
            or DriverRegistrationReceiptSchema.PriorStateEnabled
            ? value
            : throw new ArgumentException(
                $"Prior activateMultipleDrivers state '{value}' must be " +
                $"'{DriverRegistrationReceiptSchema.PriorStateAbsent}', " +
                $"'{DriverRegistrationReceiptSchema.PriorStateDisabled}', or " +
                $"'{DriverRegistrationReceiptSchema.PriorStateEnabled}'.",
                nameof(value));
    }
}

/// <summary>
/// Small atomic per-user store of LTB driver-registration receipts, keyed by
/// exact canonical driver root (ordinal-ignore-case, matching Windows driver
/// path comparison). It persists removal authority across application
/// restarts; a missing file is an empty store, while malformed content fails
/// loudly instead of silently granting or dropping ownership.
/// </summary>
public sealed class DriverRegistrationReceiptStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _path;

    public DriverRegistrationReceiptStore(string path)
    {
        _path = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(path, nameof(path));
    }

    public DriverRegistrationReceiptRecord? TryLoad(string canonicalDriverRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDriverRoot);
        return Load().FirstOrDefault(record => RootsEqual(
            record.CanonicalDriverRoot,
            canonicalDriverRoot));
    }

    public void Save(DriverRegistrationReceiptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var retained = Load()
            .Where(existing => !RootsEqual(
                existing.CanonicalDriverRoot,
                record.CanonicalDriverRoot))
            .Append(record);
        Write(retained);
    }

    public void Delete(string canonicalDriverRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDriverRoot);
        var loaded = Load();
        var retained = loaded
            .Where(existing => !RootsEqual(existing.CanonicalDriverRoot, canonicalDriverRoot))
            .ToArray();
        if (retained.Length != loaded.Count)
        {
            Write(retained);
        }
    }

    private IReadOnlyList<DriverRegistrationReceiptRecord> Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path, Utf8WithoutBom);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<DriverRegistrationReceiptRecord>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<DriverRegistrationReceiptRecord>();
        }

        StoreDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<StoreDto>(json, SerializerOptions)
                ?? throw new InvalidDataException(
                    "Driver-registration receipt store JSON must be an object, not null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Driver-registration receipt store is not valid schema-versioned JSON.",
                exception);
        }

        if (dto.SchemaVersion != DriverRegistrationReceiptSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported driver-registration receipt 'schema_version' {dto.SchemaVersion}; " +
                $"expected {DriverRegistrationReceiptSchema.CurrentVersion}.");
        }

        try
        {
            var records = dto.Receipts
                .Select(receipt => new DriverRegistrationReceiptRecord(
                    receipt.CanonicalDriverRoot,
                    receipt.PriorActivateMultipleDrivers,
                    receipt.ActivateMultipleDriversChanged,
                    receipt.SteamVrSectionWasPresent,
                    receipt.OwnershipToken))
                .ToArray();
            var duplicate = records
                .GroupBy(record => record.CanonicalDriverRoot, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            return duplicate is null
                ? records
                : throw new InvalidDataException(
                    $"Driver-registration receipt store contains duplicate canonical root '{duplicate}'.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Driver-registration receipt store contains invalid receipt data.",
                exception);
        }
    }

    private void Write(IEnumerable<DriverRegistrationReceiptRecord> records)
    {
        var dto = new StoreDto
        {
            SchemaVersion = DriverRegistrationReceiptSchema.CurrentVersion,
            Receipts = records
                .OrderBy(record => record.CanonicalDriverRoot, StringComparer.OrdinalIgnoreCase)
                .Select(record => new ReceiptDto
                {
                    CanonicalDriverRoot = record.CanonicalDriverRoot,
                    PriorActivateMultipleDrivers = record.PriorActivateMultipleDrivers,
                    ActivateMultipleDriversChanged = record.ActivateMultipleDriversChanged,
                    SteamVrSectionWasPresent = record.SteamVrSectionWasPresent,
                    OwnershipToken = record.OwnershipToken,
                })
                .ToArray(),
        };
        AtomicFileWriter.Write(_path, JsonSerializer.Serialize(dto, SerializerOptions) + "\n");
    }

    private static bool RootsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class StoreDto
    {
        [JsonPropertyName("schema_version")]
        [JsonPropertyOrder(0)]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("receipts")]
        [JsonPropertyOrder(1)]
        public required IReadOnlyList<ReceiptDto> Receipts { get; init; }
    }

    private sealed class ReceiptDto
    {
        [JsonPropertyName("canonical_driver_root")]
        [JsonPropertyOrder(0)]
        public required string CanonicalDriverRoot { get; init; }

        [JsonPropertyName("prior_activate_multiple_drivers")]
        [JsonPropertyOrder(1)]
        public required string PriorActivateMultipleDrivers { get; init; }

        [JsonPropertyName("activate_multiple_drivers_changed")]
        [JsonPropertyOrder(2)]
        public required bool ActivateMultipleDriversChanged { get; init; }

        [JsonPropertyName("steamvr_section_was_present")]
        [JsonPropertyOrder(3)]
        public required bool SteamVrSectionWasPresent { get; init; }

        [JsonPropertyName("ownership_token")]
        [JsonPropertyOrder(4)]
        public required Guid OwnershipToken { get; init; }
    }
}
