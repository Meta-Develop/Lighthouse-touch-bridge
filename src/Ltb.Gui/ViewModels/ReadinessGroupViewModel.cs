namespace Ltb.Gui.ViewModels;

public sealed record ReadinessGroupViewModel(
    string Title,
    IReadOnlyList<ReadinessRowViewModel> Rows);
