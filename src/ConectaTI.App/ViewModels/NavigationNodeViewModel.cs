using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConectaTI.App.ViewModels;

public enum NavigationNodeKind
{
    All,
    Favorites,
    Recent,
    Client,
    Group
}

public partial class NavigationNodeViewModel : ObservableObject
{
    public NavigationNodeKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ClientName { get; init; }
    public string? GroupName { get; init; }
    public int Count { get; init; }
    public string IconText { get; init; } = "•";

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<NavigationNodeViewModel> Children { get; } = new();

    public string DisplayWithCount => $"{(Kind is NavigationNodeKind.All or NavigationNodeKind.Favorites or NavigationNodeKind.Recent ? L.Text(Title) : Title)} ({Count})";
    public void RefreshDisplayProperties() => OnPropertyChanged(nameof(DisplayWithCount));
}
