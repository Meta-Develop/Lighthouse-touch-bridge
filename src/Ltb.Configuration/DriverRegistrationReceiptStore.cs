using System.Diagnostics;
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
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultLockRetryDelay = TimeSpan.FromMilliseconds(25);
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
    private readonly string _lockPath;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _lockRetryDelay;

    public DriverRegistrationReceiptStore(string path)
        : this(path, DefaultLockTimeout, DefaultLockRetryDelay)
    {
    }

    /// <summary>
    /// Creates a receipt store with a bounded exclusive-lock acquisition
    /// policy. The lock file is a persistent same-directory inode so every
    /// process contends on the same stable object.
    /// </summary>
    public DriverRegistrationReceiptStore(
        string path,
        TimeSpan lockTimeout,
        TimeSpan lockRetryDelay)
    {
        _path = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(path, nameof(path));
        ValidatePositiveFiniteDuration(lockTimeout, nameof(lockTimeout));
        ValidatePositiveFiniteDuration(lockRetryDelay, nameof(lockRetryDelay));
        _lockTimeout = lockTimeout;
        _lockRetryDelay = lockRetryDelay;
        _lockPath = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $".{Path.GetFileName(_path)}.lock");
    }

    public DriverRegistrationReceiptRecord? TryLoad(string canonicalDriverRoot)
    {
        return TryLoad(canonicalDriverRoot, CancellationToken.None);
    }

    public DriverRegistrationReceiptRecord? TryLoad(
        string canonicalDriverRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDriverRoot);
        using var storeLock = AcquireExclusiveLock(cancellationToken);
        return Load().FirstOrDefault(record => RootsEqual(
            record.CanonicalDriverRoot,
            canonicalDriverRoot));
    }

    /// <summary>
    /// Returns a complete immutable snapshot for stale-root inspection.
    /// Missing storage is an empty snapshot; malformed storage still fails
    /// loudly under the same schema validation as exact-root lookup.
    /// </summary>
    public IReadOnlyList<DriverRegistrationReceiptRecord> LoadAll()
    {
        return LoadAll(CancellationToken.None);
    }

    public IReadOnlyList<DriverRegistrationReceiptRecord> LoadAll(
        CancellationToken cancellationToken)
    {
        using var storeLock = AcquireExclusiveLock(cancellationToken);
        return Load();
    }

    public void Save(DriverRegistrationReceiptRecord record)
    {
        Save(record, CancellationToken.None);
    }

    public void Save(
        DriverRegistrationReceiptRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        SaveAll([record], cancellationToken);
    }

    /// <summary>
    /// Inserts a complete receipt set with one atomic store replacement.
    /// Existing records are accepted only when they are exactly identical;
    /// a different generation at the same case-insensitive canonical root
    /// fails closed instead of overwriting authority.
    /// </summary>
    public void SaveAll(IReadOnlyList<DriverRegistrationReceiptRecord> records)
    {
        SaveAll(records, CancellationToken.None);
    }

    public void SaveAll(
        IReadOnlyList<DriverRegistrationReceiptRecord> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        var additions = ValidateDistinctRecords(records, nameof(records));
        if (additions.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var storeLock = AcquireExclusiveLock(cancellationToken);
        var loaded = Load();
        var missing = new List<DriverRegistrationReceiptRecord>(additions.Count);
        foreach (var addition in additions.Values)
        {
            var existing = loaded.FirstOrDefault(record => RootsEqual(
                record.CanonicalDriverRoot,
                addition.CanonicalDriverRoot));
            if (existing is null)
            {
                missing.Add(addition);
                continue;
            }

            if (existing != addition)
            {
                throw new InvalidOperationException(
                    $"Driver-registration receipt root '{addition.CanonicalDriverRoot}' " +
                    "already contains a different authority generation.");
            }
        }

        if (missing.Count > 0)
        {
            WriteAndVerify(loaded.Concat(missing));
        }
    }

    /// <summary>
    /// Compatibility-only root deletion. New authority-sensitive callers
    /// should use the expected-record overload so a reused root cannot erase
    /// a different receipt generation.
    /// </summary>
    public void Delete(string canonicalDriverRoot)
    {
        Delete(canonicalDriverRoot, CancellationToken.None);
    }

    public void Delete(
        string canonicalDriverRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDriverRoot);
        DeleteAll([canonicalDriverRoot], cancellationToken);
    }

    /// <summary>
    /// Compatibility-only root deletion with one atomic store replacement.
    /// It cannot distinguish a caller's stale authority from a newer receipt
    /// at the same root.
    /// </summary>
    public void DeleteAll(IReadOnlyList<string> canonicalDriverRoots)
    {
        DeleteAll(canonicalDriverRoots, CancellationToken.None);
    }

    public void DeleteAll(
        IReadOnlyList<string> canonicalDriverRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalDriverRoots);
        var roots = canonicalDriverRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Canonical driver roots must not contain a blank value.",
                nameof(canonicalDriverRoots));
        }

        if (roots.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var storeLock = AcquireExclusiveLock(cancellationToken);
        var loaded = Load();
        var retained = loaded
            .Where(existing => !roots.Contains(existing.CanonicalDriverRoot))
            .ToArray();
        if (retained.Length != loaded.Count)
        {
            WriteAndVerify(retained);
        }
    }

    /// <summary>
    /// Deletes one receipt only if the complete current record still equals
    /// the caller's expected authority generation.
    /// </summary>
    /// <returns><see langword="true"/> when a matching record was deleted;
    /// otherwise <see langword="false"/> when it was already absent.</returns>
    public bool Delete(
        DriverRegistrationReceiptRecord expectedRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        return DeleteAll([expectedRecord], cancellationToken) == 1;
    }

    /// <summary>
    /// Deletes the expected receipt generations as one locked transaction.
    /// Any same-root generation mismatch refuses the whole batch before
    /// mutation.
    /// </summary>
    /// <returns>The number of matching records deleted.</returns>
    public int DeleteAll(
        IReadOnlyList<DriverRegistrationReceiptRecord> expectedRecords,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRecords);
        var expected = ValidateDistinctRecords(expectedRecords, nameof(expectedRecords));
        if (expected.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        using var storeLock = AcquireExclusiveLock(cancellationToken);
        var loaded = Load();
        var deletedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expectedRecord in expected.Values)
        {
            var existing = loaded.FirstOrDefault(record => RootsEqual(
                record.CanonicalDriverRoot,
                expectedRecord.CanonicalDriverRoot));
            if (existing is null)
            {
                continue;
            }

            if (existing != expectedRecord)
            {
                throw new InvalidOperationException(
                    $"Driver-registration receipt root '{expectedRecord.CanonicalDriverRoot}' " +
                    "contains a different authority generation; conditional deletion was refused.");
            }

            deletedRoots.Add(existing.CanonicalDriverRoot);
        }

        if (deletedRoots.Count > 0)
        {
            WriteAndVerify(loaded.Where(record =>
                !deletedRoots.Contains(record.CanonicalDriverRoot)));
        }

        return deletedRoots.Count;
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
                    .OrderBy(
                        record => record.CanonicalDriverRoot,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray()
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

    private void WriteAndVerify(IEnumerable<DriverRegistrationReceiptRecord> records)
    {
        var expected = records
            .OrderBy(record => record.CanonicalDriverRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dto = new StoreDto
        {
            SchemaVersion = DriverRegistrationReceiptSchema.CurrentVersion,
            Receipts = expected
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
        var verified = Load();
        if (!expected.SequenceEqual(verified))
        {
            throw new IOException(
                "Driver-registration receipt store verification did not reproduce " +
                "the complete expected authority set.");
        }
    }

    private FileStream AcquireExclusiveLock(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastContention = exception;
            }

            var remaining = _lockTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Timed out after {_lockTimeout.TotalMilliseconds:F0} ms waiting for " +
                    $"the driver-registration receipt lock '{_lockPath}'.",
                    lastContention);
            }

            var delay = remaining < _lockRetryDelay ? remaining : _lockRetryDelay;
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static Dictionary<string, DriverRegistrationReceiptRecord> ValidateDistinctRecords(
        IReadOnlyList<DriverRegistrationReceiptRecord> records,
        string parameterName)
    {
        var distinct = new Dictionary<string, DriverRegistrationReceiptRecord>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record is null)
            {
                throw new ArgumentException(
                    "Driver-registration receipt records must not contain null.",
                    parameterName);
            }

            if (!distinct.TryAdd(record.CanonicalDriverRoot, record))
            {
                throw new ArgumentException(
                    $"Driver-registration receipt records contain duplicate canonical " +
                    $"root '{record.CanonicalDriverRoot}'.",
                    parameterName);
            }
        }

        return distinct;
    }

    private static void ValidatePositiveFiniteDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The duration must be finite and greater than zero.");
        }
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
