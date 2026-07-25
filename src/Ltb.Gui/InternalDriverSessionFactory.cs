using Ltb.App;

namespace Ltb.Gui;

/// <summary>
/// GUI-owned creation boundary for a first-party application session. A new
/// session is requested for every run so stopped IPC sessions are never reused.
/// </summary>
public interface IInternalDriverSessionFactory
{
    /// <summary>
    /// False only for compatibility-only session test doubles that cannot
    /// observe production prerequisites and receive explicit deferred states.
    /// </summary>
    bool SupportsPrerequisiteProbing => false;

    IInternalDriverSession Create(InternalDriverSessionIntent intent);

    ValueTask<InternalDriverPrerequisiteSnapshot> ProbePrerequisitesAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            InternalDriverPrerequisiteSnapshot.DeferredForLegacyFactory);
}

/// <summary>
/// Production adapter over the zero-input <see cref="Ltb.App.InternalDriverSessionFactory"/>.
/// Application-data, profile, settings, log, and staged-driver paths remain
/// composed by <c>Ltb.App</c>; the GUI deliberately owns no path text fields.
/// </summary>
public sealed class InternalDriverSessionFactory : IInternalDriverSessionFactory
{
    private readonly Func<IInternalDriverPrerequisiteProbe> _prerequisiteProbeFactory;

    public InternalDriverSessionFactory()
        : this(static () =>
            Ltb.App.InternalDriverSessionFactory.CreatePrerequisiteProbe())
    {
    }

    public InternalDriverSessionFactory(
        Func<IInternalDriverPrerequisiteProbe> prerequisiteProbeFactory)
    {
        _prerequisiteProbeFactory = prerequisiteProbeFactory ??
            throw new ArgumentNullException(nameof(prerequisiteProbeFactory));
    }

    public bool SupportsPrerequisiteProbing => true;

    public IInternalDriverSession Create(InternalDriverSessionIntent intent) =>
        Ltb.App.InternalDriverSessionFactory.Create(new InternalDriverSessionOptions
        {
            Intent = intent,
        });

    public async ValueTask<InternalDriverPrerequisiteSnapshot> ProbePrerequisitesAsync(
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            async () =>
            {
                await using var probe = _prerequisiteProbeFactory() ??
                    throw new InvalidOperationException(
                        "The prerequisite probe factory returned null.");
                return await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
