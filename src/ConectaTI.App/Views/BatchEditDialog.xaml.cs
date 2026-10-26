using System.Windows;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.ViewModels;

namespace ConectaTI.App.Views;

public partial class BatchEditDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string[] _ids;
    private bool _busy;
    public BatchEditDialog(MainViewModel viewModel, IEnumerable<string> ids)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _viewModel = viewModel;
        _ids = ids.Distinct().ToArray();
        SelectionCountText.Text = _ids.Length == 1 ? L.Text("1 acesso selecionado") : L.Format("{0} acessos selecionados", (_ids.Length));
        void EnableInputs()
        {
            ClientValueBox.IsEnabled = ClientModeCombo.SelectedIndex == 1;
            GroupValueBox.IsEnabled = GroupModeCombo.SelectedIndex == 1;
            EnvironmentCombo.IsEnabled = ChangeEnvironmentCheck.IsChecked == true;
        }
        ClientModeCombo.SelectionChanged += (_, _) => EnableInputs();
        GroupModeCombo.SelectionChanged += (_, _) => EnableInputs();
        ChangeEnvironmentCheck.Checked += (_, _) => EnableInputs();
        ChangeEnvironmentCheck.Unchecked += (_, _) => EnableInputs();
        EnableInputs();
        Closing += (_, e) => e.Cancel = _busy;
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var patch = new BatchEditPatch(ClientModeCombo.SelectedIndex != 0,
            ClientModeCombo.SelectedIndex == 2 ? "" : ClientValueBox.Text,
            GroupModeCombo.SelectedIndex != 0, GroupModeCombo.SelectedIndex == 2 ? "" : GroupValueBox.Text,
            ChangeEnvironmentCheck.IsChecked == true, (EnvironmentType)EnvironmentCombo.SelectedIndex);
        if ((ClientModeCombo.SelectedIndex == 1 && string.IsNullOrWhiteSpace(patch.Client)) ||
            (GroupModeCombo.SelectedIndex == 1 && string.IsNullOrWhiteSpace(patch.Group)))
        {
            FeedbackText.Text = L.Text("Informe um valor ou escolha Limpar campo.");
            return;
        }
        if (!patch.ChangeClient && !patch.ChangeGroup && !patch.ChangeEnvironment) return;
        _busy = true; ApplyButton.IsEnabled = false;
        try
        {
            await _viewModel.ApplyBatchEditAsync(_ids, patch);
            _busy = false; DialogResult = true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordOperationFailure("Batch", ex);
            FeedbackText.Text = L.Text("A edição em lote falhou. As alterações foram desfeitas.");
        }
        finally { _busy = false; ApplyButton.IsEnabled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
}
