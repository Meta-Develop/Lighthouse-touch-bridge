namespace Ltb.App;

/// <summary>
/// Applies the two hand profiles as one transaction. Every application receipt
/// is retained before its runtime effect starts, so cancellation or failure can
/// unwind every touched hand in deterministic reverse order.
/// </summary>
internal sealed class TwoHandProfileApplicationTransaction
{
    private readonly IReliableDailyUseRuntime _runtime;
    private readonly TimeSpan _cleanupTimeout;

    public TwoHandProfileApplicationTransaction(
        IReliableDailyUseRuntime runtime,
        TimeSpan cleanupTimeout)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupTimeout, TimeSpan.Zero);

        _cleanupTimeout = cleanupTimeout;
    }

    public async Task<TwoHandProfileApplicationAttempt> ApplyAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var touched = new List<DailyUseProfileApplication>(profiles.Count);
        try
        {
            foreach (var profile in OrderAndValidateProfiles(profiles))
            {
                var application = _runtime.CreateProfileApplication(profile)
                    ?? throw new InvalidOperationException(
                        "The daily-use runtime returned a null profile-application handle.");
                if (!ReferenceEquals(application.Profile, profile) &&
                    application.Profile != profile)
                {
                    throw new InvalidOperationException(
                        "The daily-use runtime created a handle for a different profile.");
                }

                // Retain the receipt before applying: ApplyProfileAsync may throw
                // after its first external VMT or SteamVR settings effect.
                touched.Add(application);
                await _runtime.ApplyProfileAsync(application, cancellationToken)
                    .ConfigureAwait(false);
            }

            return TwoHandProfileApplicationAttempt.Succeeded(
                new TwoHandProfileApplicationLease(this, touched));
        }
        catch (Exception failure)
        {
            var rollbackFailures = await UnwindAsync(
                    touched,
                    ProfileApplicationCleanupKind.Rollback)
                .ConfigureAwait(false);
            return TwoHandProfileApplicationAttempt.Failed(failure, rollbackFailures);
        }
    }

    internal Task<IReadOnlyList<Exception>> SafeDisableAsync(
        IReadOnlyList<DailyUseProfileApplication> applications) =>
        UnwindAsync(applications, ProfileApplicationCleanupKind.Release);

    internal Task<IReadOnlyList<Exception>> RollbackAsync(
        IReadOnlyList<DailyUseProfileApplication> applications) =>
        UnwindAsync(applications, ProfileApplicationCleanupKind.Rollback);

    private static IReadOnlyList<CalibrationWizardProfileView> OrderAndValidateProfiles(
        IReadOnlyList<CalibrationWizardProfileView> profiles)
    {
        var ordered = profiles.OrderBy(profile => profile.Hand).ToArray();
        if (ordered.Length != 2 ||
            ordered.Select(profile => profile.Hand).Distinct().Count() != 2 ||
            ordered.Select(profile => profile.TrackerSerial)
                .Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new InvalidOperationException(
                "Profile application requires one profile per hand with two distinct tracker serials.");
        }

        return Array.AsReadOnly(ordered);
    }

    private async Task<IReadOnlyList<Exception>> UnwindAsync(
        IReadOnlyList<DailyUseProfileApplication> applications,
        ProfileApplicationCleanupKind cleanupKind)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var failures = new List<Exception>();
        foreach (var application in applications.Reverse())
        {
            var overrideFailure = await RunBoundedCleanupAsync(
                    token => cleanupKind == ProfileApplicationCleanupKind.Rollback
                        ? _runtime.RollbackProfileOverrideAsync(application, token)
                        : _runtime.ReleaseProfileOverrideAsync(application, token),
                    cleanupKind == ProfileApplicationCleanupKind.Rollback
                        ? $"TrackingOverride rollback for {application.Profile.Hand}"
                        : $"SafeDisable TrackingOverride release for {application.Profile.Hand}")
                .ConfigureAwait(false);
            if (overrideFailure is not null)
            {
                failures.Add(new InvalidOperationException(
                    cleanupKind == ProfileApplicationCleanupKind.Rollback
                        ? $"TrackingOverride rollback failed for {application.Profile.Hand} " +
                          $"profile '{application.Profile.ProfileName}': " +
                          overrideFailure.Message
                        : $"SafeDisable TrackingOverride release failed for " +
                          $"{application.Profile.Hand} profile " +
                          $"'{application.Profile.ProfileName}': {overrideFailure.Message}",
                    overrideFailure));
                continue;
            }

            var deactivateFailure = await RunBoundedCleanupAsync(
                    token => _runtime.DeactivateProfileAsync(application, token),
                    cleanupKind == ProfileApplicationCleanupKind.Rollback
                        ? $"rollback VMT deactivation after confirmed TrackingOverride cleanup for {application.Profile.Hand}"
                        : $"SafeDisable VMT deactivation after confirmed TrackingOverride release for {application.Profile.Hand}")
                .ConfigureAwait(false);
            if (deactivateFailure is not null)
            {
                failures.Add(new InvalidOperationException(
                    cleanupKind == ProfileApplicationCleanupKind.Rollback
                        ? $"Rollback VMT deactivation failed after confirmed TrackingOverride " +
                          $"cleanup for {application.Profile.Hand} profile " +
                          $"'{application.Profile.ProfileName}': {deactivateFailure.Message}"
                        : $"SafeDisable VMT deactivation failed after confirmed " +
                          $"TrackingOverride release for {application.Profile.Hand} profile " +
                          $"'{application.Profile.ProfileName}': {deactivateFailure.Message}",
                    deactivateFailure));
            }
        }

        return failures.AsReadOnly();
    }

    private async Task<Exception?> RunBoundedCleanupAsync(
        Func<CancellationToken, Task> operation,
        string operationName)
    {
        using var timeoutStop = new CancellationTokenSource(_cleanupTimeout);
        Task operationTask;
        try
        {
            operationTask = operation(timeoutStop.Token)
                ?? throw new InvalidOperationException(
                    $"{operationName} returned a null task.");
        }
        catch (Exception exception)
        {
            return exception;
        }

        try
        {
            await operationTask.WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException exception) when (timeoutStop.IsCancellationRequested)
        {
            ObserveLateFailure(operationTask);
            return new TimeoutException(
                $"{operationName} exceeded the {_cleanupTimeout.TotalSeconds:R}-second cleanup timeout.",
                exception);
        }
        catch (TimeoutException exception)
        {
            await timeoutStop.CancelAsync().ConfigureAwait(false);
            ObserveLateFailure(operationTask);
            return new TimeoutException(
                $"{operationName} exceeded the {_cleanupTimeout.TotalSeconds:R}-second cleanup timeout.",
                exception);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void ObserveLateFailure(Task operationTask)
    {
        _ = operationTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private enum ProfileApplicationCleanupKind
    {
        Rollback,
        Release,
    }
}

/// <summary>
/// Owns a successfully applied pair until the caller either safely releases
/// its active overrides or rolls the application settings back on abort.
/// </summary>
internal sealed class TwoHandProfileApplicationLease
{
    private readonly object _cleanupLock = new();
    private readonly TwoHandProfileApplicationTransaction _transaction;
    private Task<IReadOnlyList<Exception>>? _cleanupTask;
    private LeaseCleanupKind? _cleanupKind;

    internal TwoHandProfileApplicationLease(
        TwoHandProfileApplicationTransaction transaction,
        IReadOnlyList<DailyUseProfileApplication> applications)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        ArgumentNullException.ThrowIfNull(applications);
        Applications = Array.AsReadOnly(applications.ToArray());
    }

    public IReadOnlyList<DailyUseProfileApplication> Applications { get; }

    public Task<IReadOnlyList<Exception>> SafeDisableAsync() =>
        CleanupAsync(LeaseCleanupKind.SafeDisable);

    public Task<IReadOnlyList<Exception>> RollbackAsync() =>
        CleanupAsync(LeaseCleanupKind.Rollback);

    private Task<IReadOnlyList<Exception>> CleanupAsync(LeaseCleanupKind cleanupKind)
    {
        lock (_cleanupLock)
        {
            if (_cleanupTask is not null)
            {
                if (_cleanupKind != cleanupKind)
                {
                    throw new InvalidOperationException(
                        "The profile-application lease has already started a different cleanup mode.");
                }

                return _cleanupTask;
            }

            _cleanupKind = cleanupKind;
            _cleanupTask = cleanupKind == LeaseCleanupKind.Rollback
                ? _transaction.RollbackAsync(Applications)
                : _transaction.SafeDisableAsync(Applications);
            return _cleanupTask;
        }
    }

    private enum LeaseCleanupKind
    {
        SafeDisable,
        Rollback,
    }
}

internal sealed record TwoHandProfileApplicationAttempt(
    bool Success,
    TwoHandProfileApplicationLease? Lease,
    Exception? Failure,
    IReadOnlyList<Exception> RollbackFailures)
{
    public static TwoHandProfileApplicationAttempt Succeeded(
        TwoHandProfileApplicationLease lease) =>
        new(
            true,
            lease ?? throw new ArgumentNullException(nameof(lease)),
            null,
            Array.Empty<Exception>());

    public static TwoHandProfileApplicationAttempt Failed(
        Exception failure,
        IReadOnlyList<Exception> rollbackFailures) =>
        new(
            false,
            null,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            rollbackFailures ?? throw new ArgumentNullException(nameof(rollbackFailures)));
}
