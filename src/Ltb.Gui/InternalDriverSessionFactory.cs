using Ltb.App;

namespace Ltb.Gui;

/// <summary>
/// GUI-owned creation boundary for a first-party application session. A new
/// session is requested for every run so stopped IPC sessions are never reused.
/// </summary>
public interface IInternalDriverSessionFactory
{
    IInternalDriverSession Create(InternalDriverSessionIntent intent);
}

/// <summary>
/// Production adapter over the zero-input <see cref="Ltb.App.InternalDriverSessionFactory"/>.
/// Application-data, profile, settings, log, and staged-driver paths remain
/// composed by <c>Ltb.App</c>; the GUI deliberately owns no path text fields.
/// </summary>
public sealed class InternalDriverSessionFactory : IInternalDriverSessionFactory
{
    private readonly AppMountAdjustmentPort? _mountAdjustmentPort;

    public InternalDriverSessionFactory(AppMountAdjustmentPort? mountAdjustmentPort = null)
    {
        _mountAdjustmentPort = mountAdjustmentPort;
    }

    public IInternalDriverSession Create(InternalDriverSessionIntent intent)
    {
        var session = Ltb.App.InternalDriverSessionFactory.Create(new InternalDriverSessionOptions
        {
            Intent = intent,
        });
        _mountAdjustmentPort?.Bind(session);
        return session;
    }
}
