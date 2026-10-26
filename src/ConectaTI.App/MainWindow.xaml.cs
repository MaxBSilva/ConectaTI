using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;
using Microsoft.Win32;

namespace ConectaTI.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _incrementalSearchTimer;
    private string _incrementalSearchBuffer = string.Empty;
    private bool _isUpdatingPasswordInternally;
    private double _preferredLeftPaneRatio = double.NaN;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ViewPreferencesService.Apply(_vm.SettingsService.Current);
        InitializeComponent();
        LocalizedWindow.Attach(this);
        DataContext = _vm;

        _incrementalSearchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _incrementalSearchTimer.Tick += (_, _) =>
        {
            ResetIncrementalSearch();
        };

        _vm.ConfirmDeleteCallback = (title, message) =>
        {
            var res = MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return res == MessageBoxResult.Yes;
        };

        _vm.ShowErrorDialogCallback = (title, message) =>
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _vm.OpenExecutableSettingsCallback = () => OpenSettingsDialog(initialTabIndex: 1);
        _vm.VaultService.RequestMigrationAsync = async () => await Dispatcher.InvokeAsync(() =>
        {
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            return new VaultMigrationDialog(_vm.VaultService) { Owner=owner }.ShowDialog() == true;
        });

        RestoreWindowGeometry();

        Loaded += async (_, _) =>
        {
            UpdateResponsiveLayout();
            _vm.NotifySelectedSummaryProperties();
            SyncPasswordControls();
            UpdatePasswordPlaceholderVisibility();
            SearchTextBox.Focus();
            await _vm.VaultService.TryMigratePreviousVaultAsync();
        };

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.InlinePasswordInput)
                or nameof(MainViewModel.RevealedPasswordText)
                or nameof(MainViewModel.IsPasswordRevealed)
                or nameof(MainViewModel.HasSavedPassword)
                or nameof(MainViewModel.IsLocked))
                SyncPasswordControls();
        };
    }

    private void SyncPasswordControls()
    {
        var previous = _isUpdatingPasswordInternally;
        _isUpdatingPasswordInternally = true;
        try
        {
            var editorPassword = _vm.InlinePasswordInput;
            InlinePasswordBox.IsEnabled = true;
            if (InlinePasswordBox.Password != editorPassword) InlinePasswordBox.Password = editorPassword;
            var visible = _vm.IsPasswordRevealed ? _vm.RevealedPasswordText : string.Empty;
            if (RevealedPasswordTextBox.Text != visible) RevealedPasswordTextBox.Text = visible;
        }
        finally { _isUpdatingPasswordInternally = previous; }
        UpdatePasswordPlaceholderVisibility();
    }

    private void UpdatePasswordPlaceholderVisibility()
    {
        if (PasswordBulletsPlaceholder == null || _vm == null)
            return;

        bool showBullets = _vm.HasSavedPassword &&
                           !_vm.IsPasswordMarkedForRemoval &&
                           string.IsNullOrEmpty(InlinePasswordBox.Password);

        PasswordBulletsPlaceholder.Visibility = showBullets ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreWindowGeometry()
    {
        var s = _vm.SettingsService.Current;
        var (left, top, width, height) = SettingsService.ClampWindowBounds(
            s.WindowLeft,
            s.WindowTop,
            s.WindowWidth,
            s.WindowHeight,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (s.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        var savedLeftWidth = s.LeftPaneWidth >= 130 ? s.LeftPaneWidth : 220;
        _preferredLeftPaneRatio = s.LeftPaneRatio;
        LeftColumnDef.Width = new GridLength(Math.Min(savedLeftWidth, Math.Max(130, Width - 280)), GridUnitType.Pixel);
    }

    private void SaveWindowGeometry()
    {
        var s = _vm.SettingsService.Current;
        s.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Left;
            s.WindowTop = Top;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        else
        {
            s.WindowLeft = RestoreBounds.Left;
            s.WindowTop = RestoreBounds.Top;
            s.WindowWidth = RestoreBounds.Width;
            s.WindowHeight = RestoreBounds.Height;
        }

        if (LeftColumnDef.ActualWidth >= 130)
        {
            s.LeftPaneWidth = LeftColumnDef.ActualWidth;
        }
        s.RightPaneWidth = RightColumnDef.ActualWidth;
        s.LeftPaneRatio = _preferredLeftPaneRatio;

        _vm.SettingsService.Save();
    }

    private void MainWorkspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void AccessListPane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var condensed = AccessListPane.ActualWidth < Math.Max(200, FontSize * 14) || AccessListPane.ActualHeight < 320;
        ViewModeButtons.Visibility = condensed ? Visibility.Collapsed : Visibility.Visible;
        ViewModeMenuButton.Visibility = condensed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewModeMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = ViewModeMenuButton.ContextMenu;
        if (menu is null) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = ViewModeMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var available = MainWorkspace.ActualWidth - PaneSplitterColumnDef.ActualWidth;
        if (available > 0) _preferredLeftPaneRatio = LeftColumnDef.ActualWidth / available;
    }

    private void UpdateResponsiveLayout()
    {
        var available = MainWorkspace.ActualWidth - 4;
        if (available <= 0 || _vm is null) return;
        if (!double.IsFinite(_preferredLeftPaneRatio))
            _preferredLeftPaneRatio = Math.Clamp(_vm.SettingsService.Current.LeftPaneWidth / available, .05, .95);
        LeftColumnDef.Width = new GridLength(Math.Clamp(available * _preferredLeftPaneRatio, 130, Math.Max(130, available - 300)));
        RightColumnDef.Width = new GridLength(1, GridUnitType.Star);
    }

    // =========================================================================
    // Atalhos Globais de Teclado (Ctrl+S, Ctrl+N, Ctrl+F)
    // =========================================================================

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    await _vm.SaveConnectionAsync();
                    break;

                case Key.N:
                    e.Handled = true;
                    NewConnectionButton_Click(this, new RoutedEventArgs());
                    break;

                case Key.F:
                    e.Handled = true;
                    SearchTextBox.Focus();
                    SearchTextBox.SelectAll();
                    break;
            }
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (SearchTextBox.IsFocused || !string.IsNullOrEmpty(_vm.SearchText))
            {
                e.Handled = true;
                _vm.SearchText = string.Empty;
                SearchTextBox.Focus();
            }
        }
    }

    private async void ConnectionsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedConnection != null)
        {
            await _vm.ConnectAsync();
        }
    }

    private async void ConnectionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Não intercepta se o foco de digitação estiver em caixas de texto
        if (Keyboard.FocusedElement is TextBox or PasswordBox || e.OriginalSource is TextBox or PasswordBox)
            return;

        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right)
        {
            // O WPF mantém a navegação nativa, incluindo Ctrl/Shift e seleção múltipla.
            ResetIncrementalSearch();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _vm.SelectedConnection != null)
        {
            e.Handled = true;
            await _vm.ConnectAsync();
            return;
        }

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ResetIncrementalSearch();
            return;
        }

        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_incrementalSearchBuffer.Length > 0)
            {
                e.Handled = true;
                _incrementalSearchBuffer = _incrementalSearchBuffer.Substring(0, _incrementalSearchBuffer.Length - 1);
                if (_incrementalSearchBuffer.Length == 0)
                {
                    _incrementalSearchTimer.Stop();
                    _vm.IncrementalSearchStatus = string.Empty;
                }
                else
                {
                    _incrementalSearchTimer.Stop();
                    _incrementalSearchTimer.Start();
                    _vm.IncrementalSearchStatus = $"Buscando: {_incrementalSearchBuffer}";
                    ExecuteIncrementalSearch(_incrementalSearchBuffer);
                }
            }
            return;
        }

    }

    private void ResetIncrementalSearch()
    {
        _incrementalSearchTimer.Stop();
        _incrementalSearchBuffer = string.Empty;
        _vm.IncrementalSearchStatus = string.Empty;
    }

    private void ConnectionsListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e) => ResetIncrementalSearch();

    private void ConnectionsListBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ConnectionsListBox.IsKeyboardFocusWithin) ResetIncrementalSearch();
    }

    private void ConnectionsListBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!ConnectionsListBox.IsKeyboardFocusWithin || Keyboard.FocusedElement is TextBox or PasswordBox
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
            || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        _incrementalSearchBuffer += e.Text;
        _incrementalSearchTimer.Stop();
        _incrementalSearchTimer.Start();
        _vm.IncrementalSearchStatus = $"Buscando: {_incrementalSearchBuffer}";
        ExecuteIncrementalSearch(_incrementalSearchBuffer);
    }

    private void ExecuteIncrementalSearch(string term)
    {
        var match = ConectaTI.App.Helpers.StringNormalizationHelper.FindBestMatch(_vm.FilteredConnections, term);
        if (match != null)
        {
            var followKeyboard = ConnectionsListBox.IsKeyboardFocusWithin;
            ConnectionsListBox.SelectedItem = match;
            ConnectionsListBox.ScrollIntoView(match);
            // ScrollIntoView realiza o item virtualizado, mas não muda o ponto de navegação.
            ConnectionsListBox.UpdateLayout();
            if (followKeyboard && ConnectionsListBox.ItemContainerGenerator.ContainerFromItem(match) is ListBoxItem item)
                item.Focus();
        }
    }

    // =========================================================================
    // Pesquisa Compacta
    // =========================================================================

    private void ClearSearchTextOnly_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = string.Empty;
        SearchTextBox.Focus();
    }

    // =========================================================================
    // Ações do Formulário (Nova Conexão, Salvar, Chave, Senha)
    // =========================================================================

    private void NewConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.NewConnection();
        SyncPasswordControls();
        ConnectionNameTextBox.Focus();
        ConnectionNameTextBox.SelectAll();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SaveConnectionAsync();
        SyncPasswordControls();
    }

    private void RemovePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.MarkPasswordForRemoval();
        SyncPasswordControls();
    }

    private async void TogglePasswordRevealButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.TogglePasswordVisibilityAsync();
        SyncPasswordControls();
        if (_vm.IsPasswordRevealed)
        {
            RevealedPasswordTextBox.Focus();
            RevealedPasswordTextBox.SelectAll();
        }
        else InlinePasswordBox.Focus();
    }

    private void InlinePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPasswordInternally || _vm == null || !InlinePasswordBox.IsKeyboardFocusWithin) return;
        _vm.UpdatePasswordFromUser(InlinePasswordBox.Password);
        UpdatePasswordPlaceholderVisibility();
    }

    private void RevealedPasswordTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingPasswordInternally || _vm == null || !_vm.IsPasswordRevealed || !RevealedPasswordTextBox.IsKeyboardFocusWithin) return;
        var value = RevealedPasswordTextBox.Text;
        _vm.RevealedPasswordText = value;
        _vm.UpdatePasswordFromUser(value);
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.Text("Selecionar Arquivo de Chave Privada SSH"),
            Filter = L.Text("Chaves SSH (*.ppk;*.pem;id_rsa;id_ed25519)|*.ppk;*.pem;*id_rsa*;*id_ed25519*|Todos os arquivos (*.*)|*.*"),
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            _vm.EditingEntry.PrivateKeyPath = dlg.FileName;
            var clone = _vm.EditingEntry.Clone();
            _vm.EditingEntry = clone;
        }
    }

    // =========================================================================
    // Menu Discreto (Duplicar, Excluir, Testar conexão)
    // =========================================================================

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreActionsButton.ContextMenu != null)
        {
            MoreActionsButton.ContextMenu.PlacementTarget = MoreActionsButton;
            MoreActionsButton.ContextMenu.Placement = PlacementMode.Bottom;
            MoreActionsButton.ContextMenu.DataContext = DataContext;
            MoreActionsButton.ContextMenu.IsOpen = true;
        }
    }

    private async void MenuConnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ConnectAsync();
    }

    private async void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DuplicateConnectionAsync();
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DeleteConnectionAsync();
    }

    private async void MenuTestConnection_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RunDiagnosticAsync();
    }

    private void ConnectionsListBox_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ConnectionsListBox, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            if (!item.IsSelected) ConnectionsListBox.SelectedItem = item.DataContext;
            if (item.DataContext is ConnectionEntry entry) _vm.SelectedConnection = entry;
        }
    }
    private async void MenuCopyAddressOnly_Click(object sender, RoutedEventArgs e) => await _vm.CopySelectedAddressOnlyAsync();
    private async void MenuCopyPersistedUser_Click(object sender, RoutedEventArgs e) => await _vm.CopyPersistedUsernameAsync();
    private void ConnectionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _vm.UpdateMultipleSelection(ConnectionsListBox.SelectedItems.Count);
    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        var ids = ConnectionsListBox.SelectedItems.Cast<ConnectionEntry>().Select(c => c.Id).ToArray();
        if (ids.Length > 0 && new BatchEditDialog(_vm, ids) { Owner = this }.ShowDialog() == true)
        {
            var selectedId = _vm.SelectedConnection?.Id;
            foreach (var entry in _vm.FilteredConnections.Where(c => ids.Contains(c.Id)))
                if (!ConnectionsListBox.SelectedItems.Contains(entry)) ConnectionsListBox.SelectedItems.Add(entry);
            if (selectedId != null) _vm.SelectedConnection = _vm.FilteredConnections.FirstOrDefault(c => c.Id == selectedId);
        }
    }

    private async void MenuCopyHostPort_Click(object sender, RoutedEventArgs e)
    {
        await _vm.CopyHostAndPortAsync();
    }

    private async void MenuCopyPassword_Click(object sender, RoutedEventArgs e)
    {
        await _vm.CopyPasswordAsync();
    }

    // =========================================================================
    // Janela Centralizada de Configurações (Engrenagem)
    // =========================================================================

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsDialog(initialTabIndex: 0);
    }

    private async void OpenSettingsDialog(int initialTabIndex)
    {
        var dlg = new ExecutablesSettingsDialog(_vm.SettingsService, _vm.VaultService, _vm, initialTabIndex)
        {
            Owner = this
        };
        dlg.ShowDialog();

        if (dlg.DataModified)
        {
            await _vm.ReloadAllDataAsync(_vm.SelectedConnection?.Id);
        }
    }

    private async void MenuRestore_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RestoreSelectedConnectionAsync();
    }

    private async void MenuEmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        await _vm.EmptyTrashAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowGeometry();
        _vm.ClearSessionDrafts();
        _vm.Paths.CleanupTempRdpFiles();
    }
}
