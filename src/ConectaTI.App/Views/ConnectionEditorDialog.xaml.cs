using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ConectaTI.App.Models;
using ConectaTI.App.ViewModels;
using Microsoft.Win32;

namespace ConectaTI.App.Views;

public partial class ConnectionEditorDialog : Window
{
    private readonly MainViewModel _vm;

    public ConnectionEditorDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        var isNew = _vm.IsNewRecord;
        Title = isNew ? "Nova conexão — ConectaTI" : "Editar conexão — ConectaTI";
        DialogTitleText.Text = isNew ? "Nova conexão" : "Editar conexão";

        if (!string.IsNullOrWhiteSpace(_vm.EditingEntry.Notes))
        {
            NotesExpander.IsExpanded = true;
        }

        if (_vm.EditingEntry.Port != _vm.EditingEntry.Protocol.GetDefaultPort() ||
            !string.IsNullOrWhiteSpace(_vm.EditingEntry.Domain) ||
            !string.IsNullOrWhiteSpace(_vm.EditingEntry.PrivateKeyPath))
        {
            AdvancedExpander.IsExpanded = true;
        }

        Loaded += (_, _) => NameInputBox.Focus();
        Closing += OnWindowClosing;
    }

    private void ProtocolEditCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || !_vm.IsEditing)
            return;

        ConnectionProtocol oldProto = ConnectionProtocol.Rdp;
        if (e.RemovedItems.Count > 0)
        {
            if (e.RemovedItems[0] is SelectableOption<ConnectionProtocol> oldOpt)
                oldProto = oldOpt.Value;
            else if (e.RemovedItems[0] is ConnectionProtocol op)
                oldProto = op;
        }

        ConnectionProtocol newProto = _vm.EditingEntry.Protocol;
        if (e.AddedItems.Count > 0)
        {
            if (e.AddedItems[0] is SelectableOption<ConnectionProtocol> newOpt)
                newProto = newOpt.Value;
            else if (e.AddedItems[0] is ConnectionProtocol np)
                newProto = np;
        }

        _vm.NotifyProtocolChanged(oldProto, newProto);
    }

    private void InlinePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm != null && _vm.IsEditing)
        {
            _vm.InlinePasswordInput = InlinePasswordBox.Password;
        }
    }

    private void RemovePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.MarkPasswordForRemoval();
        InlinePasswordBox.Clear();
        _vm.MarkPasswordForRemoval();
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar Arquivo de Chave Privada SSH",
            Filter = "Chaves SSH (*.ppk;*.pem;id_rsa;id_ed25519)|*.ppk;*.pem;*id_rsa*;*id_ed25519*|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() == true)
        {
            _vm.EditingEntry.PrivateKeyPath = dlg.FileName;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await TrySaveAsync();
    }

    private async Task TrySaveAsync()
    {
        var ok = await _vm.SaveConnectionAsync();
        if (ok)
        {
            DialogResult = true;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelEdit();
        DialogResult = false;
        Close();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            await TrySaveAsync();
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _vm.HideRevealedPassword();
    }
}
