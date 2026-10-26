namespace ConectaTI.App.ViewModels;

public partial class MainViewModel : ILanguageObserver
{
    private LocalizedMessage? _statusTranslation;
    partial void OnStatusMessageChanged(string value) => _statusTranslation = L.Capture(value);
    public void OnLanguageChanged()
    {
        if (_uiDispatcher?.HasShutdownStarted == true) return;
        if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) { _uiDispatcher.InvokeAsync(OnLanguageChanged); return; }
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
            StatusMessage = L.Render(_statusTranslation, StatusMessage);
            DiagnosticResultTitle = L.Refresh(DiagnosticResultTitle);
            DiagnosticResultDetails = L.Refresh(DiagnosticResultDetails);
            foreach (var entry in _allConnections.Concat(_trashConnections).Concat(_editSessions.Values.Select(s => s.Entry))) entry.RefreshDisplayProperties();
            foreach (var node in NavigationNodes) node.RefreshDisplayProperties();
            OnPropertyChanged(string.Empty);
        }
        finally { _suppressSelectionGuard = previous; }
    }
}
