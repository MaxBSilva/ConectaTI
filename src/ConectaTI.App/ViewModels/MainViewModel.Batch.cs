using ConectaTI.App.Data;
using ConectaTI.App.Models;

namespace ConectaTI.App.ViewModels;

public partial class MainViewModel
{
    public void RecordOperationFailure(string stage, Exception exception) => _logger.RecordFailure(stage, exception);
    partial void OnCurrentViewModeChanged(ConnectionViewMode value) => OnPropertyChanged(nameof(CanBatchEdit));
    public int SelectedAccessCount { get; private set; }
    public bool CanBatchEdit => SelectedAccessCount > 0 && !IsSaving && !IsTrashMode;
    public string SelectedAccessCountText => SelectedAccessCount == 1 ? L.Text("1 acesso selecionado") : L.Format("{0} acessos selecionados", (SelectedAccessCount));
    public void UpdateMultipleSelection(int count)
    {
        SelectedAccessCount = count;
        OnPropertyChanged(nameof(SelectedAccessCount));
        OnPropertyChanged(nameof(SelectedAccessCountText));
        OnPropertyChanged(nameof(CanBatchEdit));
    }

    public async Task<int> ApplyBatchEditAsync(IReadOnlyCollection<string> ids, BatchEditPatch patch)
    {
        if (IsSaving || IsTrashMode) return 0;
        var selected = ids.Distinct().Where(id => _allConnections.Any(c => c.Id == id)).ToArray();
        var submitted = selected.Where(id => _editSessions.ContainsKey(id)).ToDictionary(id => id, id => _editSessions[id].Entry.Clone());
        IsSaving = true;
        int count;
        try { count = await _repository.ApplyBatchEditAsync(selected, patch); }
        finally { IsSaving = false; }
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
            foreach (var id in selected)
            {
                var persisted = _allConnections.First(c => c.Id == id).Clone();
                ApplyPatch(persisted, patch);
                ReplacePersistedEntry(persisted);
                if (_editSessions.TryGetValue(id, out var session))
                {
                    ApplyPatch(session.Baseline, patch);
                    var before = submitted.GetValueOrDefault(id);
                    if (before != null)
                    {
                        if (patch.ChangeClient && session.Entry.ClientName == before.ClientName) session.Entry.ClientName = patch.Client;
                        if (patch.ChangeGroup && session.Entry.GroupName == before.GroupName) session.Entry.GroupName = patch.Group;
                        if (patch.ChangeEnvironment && session.Entry.Environment == before.Environment) session.Entry.Environment = patch.Environment;
                    }
                    session.Revision++;
                }
            }
            _dataRevision++;
            var navigation = SelectedNavigationNode;
            var navigationKey = (navigation?.Kind, navigation?.ClientName, navigation?.GroupName);
            RefreshCountsAndNavigation();
            var equivalent = NavigationNodes.SelectMany(n => new[] { n }.Concat(n.Children)).FirstOrDefault(n => (n.Kind, n.ClientName, n.GroupName) == navigationKey);
            if (equivalent != null) SelectedNavigationNode = equivalent;
        }
        finally { _suppressSelectionGuard = previous; }
        ApplyFilterAndSort();
        NotifyEditState();
        StatusMessage = L.Format("Edição em lote concluída: {0} acessos atualizados.", (count));
        return count;
    }

    private static void ApplyPatch(ConnectionEntry entry, BatchEditPatch patch)
    {
        if (patch.ChangeClient) entry.ClientName = patch.Client;
        if (patch.ChangeGroup) entry.GroupName = patch.Group;
        if (patch.ChangeEnvironment) entry.Environment = patch.Environment;
    }

    public bool CanCopySelectedAddress => !string.IsNullOrWhiteSpace(SelectedConnection?.Host);
    public bool CanCopyPersistedUser => SelectedConnection?.HasEffectiveUser == true;
    public async Task CopySelectedAddressOnlyAsync()
    {
        var address = SelectedConnection?.Host;
        if (!string.IsNullOrWhiteSpace(address) && await _clipboardService.CopyTextWithRetryAsync(address))
            StatusMessage = L.Text("Endereço copiado");
    }
    public async Task CopyPersistedUsernameAsync()
    {
        var user = SelectedConnection?.RawEffectiveUser;
        if (!string.IsNullOrWhiteSpace(user) && await _clipboardService.CopyTextWithRetryAsync(user))
            StatusMessage = L.Text("Usuário copiado");
    }
}
