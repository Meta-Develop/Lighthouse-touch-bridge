namespace Ltb.Gui.ViewModels;

/// <summary>A stable binding row whose state is supplied only by typed App snapshots.</summary>
public sealed class ReadinessRowViewModel : ObservableObject
{
    private bool _isReady;
    private string _status = "Waiting";
    private string _detail = "Waiting for the first application snapshot.";

    public ReadinessRowViewModel(string key, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Key = key;
        Title = title;
    }

    public string Key { get; }

    public string Title { get; }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    internal void Update(bool isReady, string detail, string? status = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        IsReady = isReady;
        Status = status ?? (isReady ? "Ready" : "Waiting");
        Detail = detail;
    }
}
