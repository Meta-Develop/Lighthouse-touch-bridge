using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ltb.Configuration.Tests")]

namespace Ltb.Configuration;

/// <summary>Schema constants for the durable driver-registration receipt store.</summary>
public static class DriverRegistrationReceiptSchema
{
    public const int LegacyVersion = 1;

    public const int CurrentVersion = 2;

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
    Guid OwnershipToken,
    string? BuildId = null,
    string? ManifestSha256 = null,
    string? BinarySha256 = null,
    string? BuildIdSha256 = null)
{
    private static readonly Regex BuildIdPattern = new(
        @"\Adriver_ltb-[0-9]+\.[0-9]+\.[0-9]+-ipc-[0-9]+\.[0-9]+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string CanonicalDriverRoot { get; } =
        Require(CanonicalDriverRoot, nameof(CanonicalDriverRoot));

    public string PriorActivateMultipleDrivers { get; } =
        RequirePriorState(PriorActivateMultipleDrivers);

    public Guid OwnershipToken { get; init; } = RequireOwnershipToken(OwnershipToken);

    public string? BuildId { get; } = RequireBuildId(
        BuildId,
        ManifestSha256,
        BinarySha256,
        BuildIdSha256);

    public string? ManifestSha256 { get; } = RequireSha256(
        ManifestSha256,
        BuildId,
        BinarySha256,
        BuildIdSha256,
        nameof(ManifestSha256));

    public string? BinarySha256 { get; } = RequireSha256(
        BinarySha256,
        BuildId,
        ManifestSha256,
        BuildIdSha256,
        nameof(BinarySha256));

    public string? BuildIdSha256 { get; } = RequireSha256(
        BuildIdSha256,
        BuildId,
        ManifestSha256,
        BinarySha256,
        nameof(BuildIdSha256));

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

    private static Guid RequireOwnershipToken(Guid value) =>
        value != Guid.Empty
            ? value
            : throw new ArgumentException(
                "The ownership token must not be empty.",
                nameof(value));

    private static string? RequireBuildId(
        string? buildId,
        string? manifestSha256,
        string? binarySha256,
        string? buildIdSha256)
    {
        if (AllArtifactIdentityFieldsAreNull(
                buildId,
                manifestSha256,
                binarySha256,
                buildIdSha256))
        {
            return null;
        }

        if (buildId is null ||
            manifestSha256 is null ||
            binarySha256 is null ||
            buildIdSha256 is null)
        {
            throw new ArgumentException(
                "Driver artifact identity fields must be either all present or all null.");
        }

        return BuildIdPattern.IsMatch(buildId)
            ? buildId
            : throw new ArgumentException(
                "The driver build identity is blank or malformed.",
                nameof(buildId));
    }

    private static string? RequireSha256(
        string? value,
        string? buildId,
        string? firstOtherSha256,
        string? secondOtherSha256,
        string parameterName)
    {
        if (AllArtifactIdentityFieldsAreNull(
                buildId,
                value,
                firstOtherSha256,
                secondOtherSha256))
        {
            return null;
        }

        if (buildId is null ||
            value is null ||
            firstOtherSha256 is null ||
            secondOtherSha256 is null)
        {
            throw new ArgumentException(
                "Driver artifact identity fields must be either all present or all null.");
        }

        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The artifact identity must be a 64-character SHA-256 value.",
                parameterName);
        }

        return value.ToLowerInvariant();
    }

    private static bool AllArtifactIdentityFieldsAreNull(
        string? buildId,
        string? manifestSha256,
        string? binarySha256,
        string? buildIdSha256) =>
        buildId is null &&
        manifestSha256 is null &&
        binarySha256 is null &&
        buildIdSha256 is null;
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
    private const int UnixErrorTryAgain = 11;
    private const int WindowsErrorSharingViolation = 32;
    private const int WindowsErrorLockViolation = 33;
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
    private readonly Action? _afterLockedLoad;
    private readonly Action? _beforeAtomicWrite;

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
        : this(
            path,
            lockTimeout,
            lockRetryDelay,
            afterLockedLoad: null,
            beforeAtomicWrite: null)
    {
    }

    internal DriverRegistrationReceiptStore(
        string path,
        TimeSpan lockTimeout,
        TimeSpan lockRetryDelay,
        Action? afterLockedLoad,
        Action? beforeAtomicWrite)
    {
        _path = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(path, nameof(path));
        ValidatePositiveFiniteDuration(lockTimeout, nameof(lockTimeout));
        ValidatePositiveFiniteDuration(lockRetryDelay, nameof(lockRetryDelay));
        _lockTimeout = lockTimeout;
        _lockRetryDelay = lockRetryDelay;
        _lockPath = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $".{Path.GetFileName(_path)}.lock");
        _afterLockedLoad = afterLockedLoad;
        _beforeAtomicWrite = beforeAtomicWrite;
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
        _afterLockedLoad?.Invoke();
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
        _afterLockedLoad?.Invoke();
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
        _afterLockedLoad?.Invoke();
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

        if (dto.SchemaVersion is not
            (DriverRegistrationReceiptSchema.LegacyVersion or
                DriverRegistrationReceiptSchema.CurrentVersion))
        {
            throw new InvalidDataException(
                $"Unsupported driver-registration receipt 'schema_version' {dto.SchemaVersion}; " +
                $"expected {DriverRegistrationReceiptSchema.LegacyVersion} or " +
                $"{DriverRegistrationReceiptSchema.CurrentVersion}.");
        }

        try
        {
            var receipts = dto.Receipts
                ?? throw new InvalidDataException(
                    "Driver-registration receipt store 'receipts' must be an array.");
            if (receipts.Any(receipt => receipt is null))
            {
                throw new InvalidDataException(
                    "Driver-registration receipt store 'receipts' must not contain null.");
            }

            if (dto.SchemaVersion == DriverRegistrationReceiptSchema.LegacyVersion &&
                receipts.Any(receipt =>
                    receipt!.BuildId is not null ||
                    receipt.ManifestSha256 is not null ||
                    receipt.BinarySha256 is not null ||
                    receipt.BuildIdSha256 is not null))
            {
                throw new InvalidDataException(
                    "Schema-v1 receipts must not contain artifact identity fields.");
            }

            var records = receipts
                .Select(receipt => new DriverRegistrationReceiptRecord(
                    receipt!.CanonicalDriverRoot,
                    receipt.PriorActivateMultipleDrivers,
                    receipt.ActivateMultipleDriversChanged,
                    receipt.SteamVrSectionWasPresent,
                    receipt.OwnershipToken,
                    dto.SchemaVersion == DriverRegistrationReceiptSchema.LegacyVersion
                        ? null
                        : receipt.BuildId,
                    dto.SchemaVersion == DriverRegistrationReceiptSchema.LegacyVersion
                        ? null
                        : receipt.ManifestSha256,
                    dto.SchemaVersion == DriverRegistrationReceiptSchema.LegacyVersion
                        ? null
                        : receipt.BinarySha256,
                    dto.SchemaVersion == DriverRegistrationReceiptSchema.LegacyVersion
                        ? null
                        : receipt.BuildIdSha256))
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
                    BuildId = record.BuildId,
                    ManifestSha256 = record.ManifestSha256,
                    BinarySha256 = record.BinarySha256,
                    BuildIdSha256 = record.BuildIdSha256,
                })
                .ToArray(),
        };
        _beforeAtomicWrite?.Invoke();
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
            catch (IOException exception) when (IsLockContention(exception))
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

    internal static bool IsLockContention(IOException exception)
    {
        var nativeErrorCode = exception.HResult & 0xFFFF;
        return nativeErrorCode is
            UnixErrorTryAgain or
            WindowsErrorSharingViolation or
            WindowsErrorLockViolation;
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

            if (record.OwnershipToken == Guid.Empty)
            {
                throw new ArgumentException(
                    "Driver-registration receipt records must not contain an empty " +
                    "ownership token.",
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
        public required IReadOnlyList<ReceiptDto?>? Receipts { get; init; }
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

        [JsonPropertyName("build_id")]
        [JsonPropertyOrder(5)]
        public string? BuildId { get; init; }

        [JsonPropertyName("manifest_sha256")]
        [JsonPropertyOrder(6)]
        public string? ManifestSha256 { get; init; }

        [JsonPropertyName("binary_sha256")]
        [JsonPropertyOrder(7)]
        public string? BinarySha256 { get; init; }

        [JsonPropertyName("build_id_sha256")]
        [JsonPropertyOrder(8)]
        public string? BuildIdSha256 { get; init; }
    }
}
