using System.Collections.Concurrent;
using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.Gui;
using Ltb.Gui.ViewModels;
using Ltb.Protocol;

namespace Ltb.Gui.Tests;

public sealed class AppMountAdjustmentPortTests
{
    [Fact]
    public async Task ConcreteAdapterMapsLiveApplyAndSaveToBoundAppControl()
    {
        var baseLeft = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            new Vector3(0.1f, 0f, 0f));
        var session = new FakeAppSession(MountSnapshot(baseLeft));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);

        Assert.True(port.CurrentSnapshot.IsAvailable);
        Assert.Equal(0.1f, port.CurrentSnapshot.Left.BaseMount.TranslationMeters.X, 6);

        var trackerSide = new MountAdjustmentTransform(
            new Vector3(0.01f, 0f, 0f),
            Quaternion.Identity);
        var controllerSide = new MountAdjustmentTransform(
            new Vector3(0.2f, 0f, 0f),
            Quaternion.Identity);
        var live = await port.ApplyLiveAsync(new MountAdjustmentLiveApplyRequest(
            Revision: 8,
            MountAdjustmentHand.Left,
            new MountAdjustmentPair(trackerSide, controllerSide)));

        Assert.True(live.Succeeded, live.Diagnostic);
        Assert.Equal(8, live.AcknowledgedRevision);
        var applied = Assert.Single(session.ApplyRequests);
        Assert.Equal(ProtocolHand.Left, applied.Hand);
        Assert.Equal(new Vector3(0.01f, 0f, 0f), applied.Adjustment.TrackerSideAdjustment.TranslationMeters);
        Assert.Equal(new Vector3(0.2f, 0f, 0f), applied.Adjustment.ControllerSideAdjustment.TranslationMeters);
        Assert.Equal(
            0.11f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.X,
            5);
        Assert.Equal(
            0.2f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.Y,
            5);
        Assert.Equal(
            0f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.Z,
            5);

        var save = await port.SaveAsync(new MountAdjustmentSaveRequest(
            Revision: 9,
            live.Snapshot.Left.AppliedAdjustments,
            MountAdjustmentPair.Identity));

        Assert.True(save.Succeeded, save.Diagnostic);
        Assert.Equal(9, save.Snapshot.Revision);
        Assert.Equal(live.Snapshot.Left.AppliedAdjustments, save.Snapshot.Left.SavedAdjustments);
        Assert.Equal(1, session.SaveCount);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotTurnPersistedSaveIntoFailure()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        MountAdjustmentSnapshot? laterObservation = null;
        port.SnapshotChanged += (_, _) =>
            throw new InvalidOperationException("scripted early GUI observer failure");
        port.SnapshotChanged += (_, snapshot) => laterObservation = snapshot;

        var result = await port.SaveAsync(new MountAdjustmentSaveRequest(
            Revision: 8,
            MountAdjustmentPair.Identity,
            MountAdjustmentPair.Identity));

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(1, session.SaveCount);
        Assert.Equal(8, port.CurrentSnapshot.Revision);
        Assert.Equal(port.CurrentSnapshot, laterObservation);
        Assert.Equal(port.CurrentSnapshot, result.Snapshot);
    }

    [Fact]
    public void ConcreteAdapterMapsRestoreFailureAndExplicitRestoreClear()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        var updates = new List<MountAdjustmentSnapshot>();
        port.SnapshotChanged += (_, snapshot) => updates.Add(snapshot);

        session.PublishSession(InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "restore failed visibly",
                ["restore failed visibly"]),
        });

        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Failure,
            updates[^1].RestoreWarning.Kind);
        Assert.Contains("restore failed visibly", updates[^1].RestoreWarning.Message);

        session.PublishSession(InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.Restored,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "restored exactly",
                Array.Empty<string>()),
        });

        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Clear,
            updates[^1].RestoreWarning.Kind);
    }

    [Fact]
    public async Task ConcurrentMountAndSessionCallbacksPublishInAuthoritativeOrder()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        using var releaseFirstCallback = new ManualResetEventSlim(initialState: false);
        var firstCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<MountAdjustmentSnapshot>();
        var blocked = 0;
        port.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Revision == 8 &&
                Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                firstCallbackEntered.TrySetResult();
                if (!releaseFirstCallback.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("The ordered callback test was not released.");
                }
            }

            updates.Add(snapshot);
        };
        var adjusted = MountSnapshot(
            new RigidTransform(
                Quaternion.Identity,
                new Vector3(0.01f, 0f, 0f)),
            revision: 8);
        var mountPublication = Task.Run(() => session.PublishMount(adjusted));
        await firstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var restoreFailed = InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "concurrent restore failure",
                ["concurrent restore failure"]),
        };
        var sessionPublication = Task.Run(() => session.PublishSession(restoreFailed));
        await Task.Delay(25);
        Assert.False(sessionPublication.IsCompleted);

        releaseFirstCallback.Set();
        await Task.WhenAll(mountPublication, sessionPublication)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, updates.Count);
        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Failure,
            updates[^1].RestoreWarning.Kind);
        Assert.Equal(port.CurrentSnapshot, updates[^1]);
    }

    [Fact]
    public async Task DelayedSaveAcknowledgementCannotOverwriteNewerTeardown()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        var delayDispatch = false;
        var delayedDispatches = new ConcurrentQueue<Action>();
        using var viewModel = new MountAdjustmentViewModel(
            port,
            action =>
            {
                if (delayDispatch)
                {
                    delayedDispatches.Enqueue(action);
                }
                else
                {
                    action();
                }
            });
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        Assert.True(viewModel.IsDirty);
        session.BlockNextSave();
        var staleSaveSnapshot = session.CurrentMountAdjustment;

        var save = viewModel.SaveAsync();
        await session.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        delayDispatch = true;
        session.PublishMount(session.CurrentMountAdjustment with
        {
            Revision = 9,
            IsAvailable = false,
        });

        session.CompleteBlockedSave(new InternalDriverMountAdjustmentResult(
            AcknowledgedRevision: 8,
            Succeeded: true,
            "stale save completed",
            staleSaveSnapshot));
        await save.WaitAsync(TimeSpan.FromSeconds(2));
        while (delayedDispatches.TryDequeue(out var dispatch))
        {
            dispatch();
        }

        Assert.Equal(9, port.CurrentSnapshot.Revision);
        Assert.False(port.CurrentSnapshot.IsAvailable);
        Assert.False(viewModel.IsAvailable);
    }

    [Fact]
    public void RebindingRemapsLowerLocalRevisionsIntoMonotonicViewModelSnapshots()
    {
        var sessionA = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.1f, 0f, 0f)),
            revision: 0));
        var port = new AppMountAdjustmentPort();
        port.Bind(sessionA);
        using var viewModel = new MountAdjustmentViewModel(port, action => action());
        var revisions = new List<long>();
        port.SnapshotChanged += (_, snapshot) => revisions.Add(snapshot.Revision);

        sessionA.PublishMount(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.2f, 0f, 0f)),
            revision: 9));
        sessionA.PublishMount(sessionA.CurrentMountAdjustment with
        {
            Revision = 10,
            IsAvailable = false,
        });
        var retiredRevision = port.CurrentSnapshot.Revision;

        var sessionB = new FakeAppSession(MountSnapshot(
            RigidTransform.Identity,
            revision: 0,
            isAvailable: false));
        port.Bind(sessionB);
        var boundRevision = port.CurrentSnapshot.Revision;
        sessionB.PublishMount(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.3f, 0f, 0f)),
            revision: 1));

        Assert.True(boundRevision > retiredRevision);
        Assert.True(port.CurrentSnapshot.Revision > boundRevision);
        Assert.All(
            revisions.Zip(revisions.Skip(1)),
            pair => Assert.True(
                pair.First <= pair.Second,
                $"Published revision decreased from {pair.First} to {pair.Second}."));
        Assert.True(viewModel.IsAvailable);
        Assert.Contains("300.0", viewModel.LeftHand.BaseMountTransform);
        Assert.Equal(
            0.3f,
            port.CurrentSnapshot.Left.BaseMount.TranslationMeters.X,
            6);
    }

    [Fact]
    public void CapturedOldCallbacksAndWrongSenderCannotMutateNewBinding()
    {
        var sessionA = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.1f, 0f, 0f))));
        var port = new AppMountAdjustmentPort();
        port.Bind(sessionA);
        var oldSessionCallback = sessionA.CaptureSessionCallback();
        var oldMountCallback = sessionA.CaptureMountCallback();

        var sessionB = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.2f, 0f, 0f)),
            revision: 1));
        port.Bind(sessionB);
        var currentMountCallback = sessionB.CaptureMountCallback();
        var expected = port.CurrentSnapshot;
        var publications = 0;
        port.SnapshotChanged += (_, _) => publications++;

        oldMountCallback(
            sessionA,
            MountSnapshot(
                new RigidTransform(Quaternion.Identity, new Vector3(0.9f, 0f, 0f)),
                revision: 100));
        oldSessionCallback(
            sessionA,
            InternalDriverSessionSnapshot.Initial with
            {
                TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                    InternalDriverTrackerNeutralizationState.RestoreFailed,
                    Array.Empty<InternalDriverTrackerPath>(),
                    BackendSnapshotId: null,
                    "stale restore failure",
                    ["stale restore failure"]),
            });
        currentMountCallback(
            sessionA,
            MountSnapshot(
                new RigidTransform(Quaternion.Identity, new Vector3(0.8f, 0f, 0f)),
                revision: 101));

        Assert.Equal(0, publications);
        Assert.Equal(expected, port.CurrentSnapshot);
    }

    [Fact]
    public async Task DelayedApplyCompletionAfterRebindReturnsStaleFailureWithCurrentSnapshot()
    {
        var sessionA = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(sessionA);
        sessionA.BlockNextApply();
        var request = new MountAdjustmentLiveApplyRequest(
            Revision: 8,
            MountAdjustmentHand.Left,
            new MountAdjustmentPair(
                new MountAdjustmentTransform(
                    new Vector3(0.01f, 0f, 0f),
                    Quaternion.Identity),
                MountAdjustmentTransform.Identity));

        var pending = port.ApplyLiveAsync(request).AsTask();
        await sessionA.ApplyEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var sessionB = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.2f, 0f, 0f)),
            revision: 0));
        port.Bind(sessionB);
        var expected = port.CurrentSnapshot;
        var publications = 0;
        port.SnapshotChanged += (_, _) => publications++;
        sessionA.CompleteBlockedApply(new InternalDriverMountAdjustmentResult(
            request.Revision,
            Succeeded: true,
            "old apply completed",
            MountSnapshot(
                new RigidTransform(Quaternion.Identity, new Vector3(0.9f, 0f, 0f)),
                request.Revision)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(request.Revision, result.AcknowledgedRevision);
        Assert.Contains("stale", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, result.Snapshot);
        Assert.Equal(expected, port.CurrentSnapshot);
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task DelayedSaveCompletionAfterRebindReturnsStaleFailureWithCurrentSnapshot()
    {
        var sessionA = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(sessionA);
        sessionA.BlockNextSave();
        var request = new MountAdjustmentSaveRequest(
            Revision: 8,
            MountAdjustmentPair.Identity,
            MountAdjustmentPair.Identity);

        var pending = port.SaveAsync(request).AsTask();
        await sessionA.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var sessionB = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.2f, 0f, 0f)),
            revision: 0));
        port.Bind(sessionB);
        var expected = port.CurrentSnapshot;
        var publications = 0;
        port.SnapshotChanged += (_, _) => publications++;
        sessionA.CompleteBlockedSave(new InternalDriverMountAdjustmentResult(
            request.Revision,
            Succeeded: true,
            "old save completed",
            MountSnapshot(
                new RigidTransform(Quaternion.Identity, new Vector3(0.9f, 0f, 0f)),
                request.Revision)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(request.Revision, result.AcknowledgedRevision);
        Assert.Contains("stale", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, result.Snapshot);
        Assert.Equal(expected, port.CurrentSnapshot);
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task DelayedSaveCompletionAfterRebindCannotClearNewViewModelDirtyState()
    {
        var sessionA = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(sessionA);
        using var viewModel = new MountAdjustmentViewModel(port, action => action());
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        await WaitUntilAsync(
            () => sessionA.ApplyRequests.Count == 1,
            TimeSpan.FromSeconds(2));
        sessionA.BlockNextSave();

        var pending = viewModel.SaveAsync();
        await sessionA.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var requestRevision = Assert.Single(sessionA.SaveRevisions);
        var sessionB = new FakeAppSession(MountSnapshot(
            new RigidTransform(Quaternion.Identity, new Vector3(0.2f, 0f, 0f)),
            revision: 0));
        port.Bind(sessionB);
        viewModel.LeftHand.TrackerSide.PositionYMillimeters = 15d;
        Assert.True(viewModel.IsDirty);
        sessionA.CompleteBlockedSave(new InternalDriverMountAdjustmentResult(
            requestRevision,
            Succeeded: true,
            "old save completed",
            MountSnapshot(
                new RigidTransform(Quaternion.Identity, new Vector3(0.9f, 0f, 0f)),
                requestRevision)));

        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => sessionB.ApplyRequests.Count == 1,
            TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsDirty);
        Assert.Equal(15d, viewModel.LeftHand.TrackerSide.PositionYMillimeters);
        Assert.Equal(
            0.015f,
            port.CurrentSnapshot.Left.AppliedAdjustments.TrackerSide
                .TranslationMeters.Y,
            6);
    }

    [Fact]
    public async Task CurrentBindingAcknowledgementsPassThroughAndSaveClearsDirtyState()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        using var viewModel = new MountAdjustmentViewModel(port, action => action());

        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        await WaitUntilAsync(
            () => session.ApplyRequests.Count == 1 &&
                  viewModel.StatusText.Contains("applied", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(2));
        var applied = Assert.Single(session.ApplyRequests);

        Assert.True(viewModel.IsDirty);
        Assert.Equal(8, applied.Revision);
        await viewModel.SaveAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsDirty);
        Assert.Equal(new[] { applied.Revision }, session.SaveRevisions);
        Assert.Equal(applied.Revision, port.CurrentSnapshot.Revision);
    }

    [Fact]
    public async Task SameValueSessionRefreshRetainsDirtyBufferAtSameExternalRevision()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        using var viewModel = new MountAdjustmentViewModel(port, action => action());
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        await WaitUntilAsync(
            () => session.ApplyRequests.Count == 1,
            TimeSpan.FromSeconds(2));
        var revision = port.CurrentSnapshot.Revision;

        session.PublishSession(InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.Active,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "same-value lifecycle refresh",
                Array.Empty<string>()),
        });

        Assert.Equal(revision, port.CurrentSnapshot.Revision);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(10d, viewModel.LeftHand.TrackerSide.PositionXMillimeters);
        Assert.Contains("same-value lifecycle refresh", viewModel.TrackerNeutralizationStatusText);
    }

    [Fact]
    public async Task UnavailableBindingRejectsApplyAndSaveBeforeCallingControl()
    {
        var session = new FakeAppSession(MountSnapshot(
            RigidTransform.Identity,
            isAvailable: false));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);

        var apply = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.ApplyLiveAsync(new MountAdjustmentLiveApplyRequest(
                Revision: 8,
                MountAdjustmentHand.Left,
                MountAdjustmentPair.Identity)));
        var save = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.SaveAsync(new MountAdjustmentSaveRequest(
                Revision: 8,
                MountAdjustmentPair.Identity,
                MountAdjustmentPair.Identity)));

        Assert.Contains("unavailable", apply.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", save.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.ApplyRequests);
        Assert.Equal(0, session.SaveCount);
    }

    [Fact]
    public async Task AppPublicationCallbackCannotDeadlockControlDispatch()
    {
        var session = new FakeAppSession(
            MountSnapshot(RigidTransform.Identity),
            serializeControlWithPublication: true);
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        var publication = session.PublishMountWhileHoldingPublicationLockAsync(
            session.CurrentMountAdjustment);
        await session.PublicationLockEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var request = new MountAdjustmentLiveApplyRequest(
            Revision: 8,
            MountAdjustmentHand.Left,
            new MountAdjustmentPair(
                new MountAdjustmentTransform(
                    new Vector3(0.01f, 0f, 0f),
                    Quaternion.Identity),
                MountAdjustmentTransform.Identity));

        var operation = Task.Run(async () => await port.ApplyLiveAsync(request));
        await session.ControlCallEntered.WaitAsync(TimeSpan.FromSeconds(2));
        session.ReleasePublicationCallback();
        await Task.WhenAll(publication, operation).WaitAsync(TimeSpan.FromSeconds(2));
        var result = await operation;

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(request.Revision, result.AcknowledgedRevision);
        Assert.Equal(request.Revision, port.CurrentSnapshot.Revision);
    }

    private static InternalDriverMountAdjustmentSnapshot MountSnapshot(
        RigidTransform baseLeft,
        long revision = 7,
        bool isAvailable = true)
    {
        var left = new InternalDriverMountAdjustmentHandSnapshot(
            baseLeft,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            baseLeft);
        var right = new InternalDriverMountAdjustmentHandSnapshot(
            RigidTransform.Identity,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            RigidTransform.Identity);
        return new InternalDriverMountAdjustmentSnapshot(
            revision,
            isAvailable,
            left,
            right);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected test condition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeAppSession :
        IInternalDriverSession,
        IInternalDriverMountAdjustmentControl
    {
        private readonly object _appPublicationSync = new();
        private readonly bool _serializeControlWithPublication;
        private readonly TaskCompletionSource _publicationLockEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releasePublicationCallback = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _controlCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private InternalDriverSessionSnapshot _session =
            InternalDriverSessionSnapshot.Initial;
        private InternalDriverMountAdjustmentSnapshot _mount;
        private TaskCompletionSource<InternalDriverMountAdjustmentResult>?
            _blockedApply;
        private TaskCompletionSource? _applyEntered;
        private TaskCompletionSource<InternalDriverMountAdjustmentResult>?
            _blockedSave;
        private TaskCompletionSource? _saveEntered;

        public FakeAppSession(
            InternalDriverMountAdjustmentSnapshot mount,
            bool serializeControlWithPublication = false)
        {
            _mount = mount;
            _serializeControlWithPublication = serializeControlWithPublication;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public event EventHandler<InternalDriverMountAdjustmentSnapshot>?
            MountAdjustmentChanged;

        public List<(long Revision, ProtocolHand Hand, MountAdjustment Adjustment)> ApplyRequests
        {
            get;
        } = [];

        public int SaveCount { get; private set; }

        public List<long> SaveRevisions { get; } = [];

        public Task ApplyEntered => _applyEntered?.Task ??
            throw new InvalidOperationException("No apply is blocked.");

        public Task PublicationLockEntered => _publicationLockEntered.Task;

        public Task ControlCallEntered => _controlCallEntered.Task;

        public Task SaveEntered => _saveEntered?.Task ??
            throw new InvalidOperationException("No save is blocked.");

        public InternalDriverSessionSnapshot CurrentSnapshot => _session;

        public InternalDriverMountAdjustmentSnapshot CurrentMountAdjustment => _mount;

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<InternalDriverMountAdjustmentResult> ApplyMountAdjustmentAsync(
            long revision,
            ProtocolHand hand,
            MountAdjustment adjustment,
            CancellationToken cancellationToken = default)
        {
            ApplyRequests.Add((revision, hand, adjustment));
            if (_serializeControlWithPublication)
            {
                _controlCallEntered.TrySetResult();
                lock (_appPublicationSync)
                {
                    return ApplyMountAdjustmentCore(revision, hand, adjustment);
                }
            }

            return ApplyMountAdjustmentCore(revision, hand, adjustment);
        }

        private ValueTask<InternalDriverMountAdjustmentResult> ApplyMountAdjustmentCore(
            long revision,
            ProtocolHand hand,
            MountAdjustment adjustment)
        {
            if (_blockedApply is not null)
            {
                _applyEntered!.TrySetResult();
                return new ValueTask<InternalDriverMountAdjustmentResult>(
                    _blockedApply.Task);
            }

            var selected = hand == ProtocolHand.Left ? _mount.Left : _mount.Right;
            var updated = selected with
            {
                AppliedAdjustments = adjustment,
                EffectiveMount = adjustment.ApplyTo(selected.BaseMount),
            };
            _mount = hand == ProtocolHand.Left
                ? _mount with { Revision = revision, Left = updated }
                : _mount with { Revision = revision, Right = updated };
            MountAdjustmentChanged?.Invoke(this, _mount);
            return ValueTask.FromResult(new InternalDriverMountAdjustmentResult(
                revision,
                true,
                "applied",
                _mount));
        }

        public ValueTask<InternalDriverMountAdjustmentResult> SaveMountAdjustmentsAsync(
            long revision,
            MountAdjustment left,
            MountAdjustment right,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            SaveRevisions.Add(revision);
            if (_blockedSave is not null)
            {
                _saveEntered!.TrySetResult();
                return new ValueTask<InternalDriverMountAdjustmentResult>(
                    _blockedSave.Task);
            }

            _mount = _mount with
            {
                Revision = revision,
                Left = _mount.Left with
                {
                    AppliedAdjustments = left,
                    SavedAdjustments = left,
                    EffectiveMount = left.ApplyTo(_mount.Left.BaseMount),
                },
                Right = _mount.Right with
                {
                    AppliedAdjustments = right,
                    SavedAdjustments = right,
                    EffectiveMount = right.ApplyTo(_mount.Right.BaseMount),
                },
            };
            MountAdjustmentChanged?.Invoke(this, _mount);
            return ValueTask.FromResult(new InternalDriverMountAdjustmentResult(
                revision,
                true,
                "saved",
                _mount));
        }

        public void BlockNextApply()
        {
            _blockedApply =
                new TaskCompletionSource<InternalDriverMountAdjustmentResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            _applyEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void CompleteBlockedApply(InternalDriverMountAdjustmentResult result)
        {
            var completion = _blockedApply ??
                throw new InvalidOperationException("No apply is blocked.");
            _blockedApply = null;
            _applyEntered = null;
            completion.TrySetResult(result);
        }

        public void BlockNextSave()
        {
            _blockedSave =
                new TaskCompletionSource<InternalDriverMountAdjustmentResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            _saveEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void CompleteBlockedSave(InternalDriverMountAdjustmentResult result)
        {
            var completion = _blockedSave ??
                throw new InvalidOperationException("No save is blocked.");
            _blockedSave = null;
            _saveEntered = null;
            completion.TrySetResult(result);
        }

        public void PublishSession(InternalDriverSessionSnapshot snapshot)
        {
            _session = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public void PublishMount(InternalDriverMountAdjustmentSnapshot snapshot)
        {
            _mount = snapshot;
            MountAdjustmentChanged?.Invoke(this, snapshot);
        }

        public Task PublishMountWhileHoldingPublicationLockAsync(
            InternalDriverMountAdjustmentSnapshot snapshot) =>
            Task.Run(() =>
            {
                lock (_appPublicationSync)
                {
                    _publicationLockEntered.TrySetResult();
                    if (!_releasePublicationCallback.Task.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException(
                            "The scripted App publication callback was not released.");
                    }

                    PublishMount(snapshot);
                }
            });

        public void ReleasePublicationCallback() =>
            _releasePublicationCallback.TrySetResult();

        public EventHandler<InternalDriverSessionSnapshot> CaptureSessionCallback() =>
            SnapshotChanged ??
            throw new InvalidOperationException("No session callback is subscribed.");

        public EventHandler<InternalDriverMountAdjustmentSnapshot> CaptureMountCallback() =>
            MountAdjustmentChanged ??
            throw new InvalidOperationException("No mount callback is subscribed.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
