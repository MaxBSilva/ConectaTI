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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        _incrementalSearchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _incrementalSearchTimer.Tick += (_, _) =>
        {
            _incrementalSearchTimer.Stop();
            _incrementalSearchBuffer = string.Empty;
            _vm.IncrementalSearchStatus = string.Empty;
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
        _vm.RequestMasterPasswordMigrationCallback = PromptForMigrationPasswordAsync;

        RestoreWindowGeometry();

        Loaded += (_, _) =>
        {
            _vm.NotifySelectedSummaryProperties();
            UpdatePasswordPlaceholderVisibility();
            SearchTextBox.Focus();
        };

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.InlinePasswordInput)
                or nameof(MainViewModel.RevealedPasswordText)
                or nameof(MainViewModel.IsPasswordRevealed)
                or nameof(MainViewModel.HasSavedPassword))
                SyncPasswordControls();
        };
    }

    private void SyncPasswordControls()
    {
        var previous = _isUpdatingPasswordInternally;
        _isUpdatingPasswordInternally = true;
        try
        {
            if (InlinePasswordBox.Password != _vm.InlinePasswordInput)
                InlinePasswordBox.Password = _vm.InlinePasswordInput;
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
        LeftColumnDef.Width = new GridLength(savedLeftWidth, GridUnitType.Pixel);
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

        _vm.SettingsService.Save();
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
        if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox)
            return;

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _vm.SelectedConnection != null)
        {
            e.Handled = true;
            await _vm.ConnectAsync();
            return;
        }

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _incrementalSearchTimer.Stop();
            _incrementalSearchBuffer = string.Empty;
            _vm.IncrementalSearchStatus = string.Empty;
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

        var keyChar = GetCharFromKey(e.Key);
        if (keyChar.HasValue && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _incrementalSearchBuffer += keyChar.Value;
            _incrementalSearchTimer.Stop();
            _incrementalSearchTimer.Start();
            _vm.IncrementalSearchStatus = $"Buscando: {_incrementalSearchBuffer}";
            ExecuteIncrementalSearch(_incrementalSearchBuffer);
        }
    }

    private void ExecuteIncrementalSearch(string term)
    {
        var match = ConectaTI.App.Helpers.StringNormalizationHelper.FindBestMatch(_vm.FilteredConnections, term);
        if (match != null)
        {
            if (_vm.SelectedConnection?.Id != match.Id)
            {
                _ = _vm.RequestNavigateToConnectionAsync(match);
            }
            ConnectionsListBox.ScrollIntoView(match);
        }
    }

    private static char? GetCharFromKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return (char)('a' + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9)
            return (char)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return (char)('0' + (key - Key.NumPad0));
        if (key == Key.Space)
            return ' ';
        if (key == Key.OemMinus || key == Key.Subtract)
            return '-';
        if (key == Key.OemPeriod || key == Key.Decimal)
            return '.';
        return null;
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
            Title = "Selecionar Arquivo de Chave Privada SSH",
            Filter = "Chaves SSH (*.ppk;*.pem;id_rsa;id_ed25519)|*.ppk;*.pem;*id_rsa*;*id_ed25519*|Todos os arquivos (*.*)|*.*",
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

    private Task<string?> PromptForMigrationPasswordAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        Dispatcher.Invoke(() =>
        {
            var win = new Window
            {
                Title = "Migração de Credenciais — ConectaTI",
                Width = 430,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = TryFindResource("AppBackgroundBrush") as Brush ?? Brushes.White,
                Foreground = TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.Black
            };

            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock
            {
                Text = "O aplicativo foi atualizado para remover o bloqueio por senha mestra.\nDigite sua senha mestra anterior para migrar as credenciais com segurança para a proteção nativa do Windows (DPAPI):",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 12
            });

            var pwdBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(6, 4, 6, 4) };
            stack.Children.Add(pwdBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Migrar", Width = 80, Padding = new Thickness(0, 5, 0, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancelar", Width = 80, Padding = new Thickness(0, 5, 0, 5), IsCancel = true };

            string? result = null;
            okBtn.Click += (_, _) => { result = pwdBox.Password; win.DialogResult = true; win.Close(); };
            cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            stack.Children.Add(btnPanel);

            win.Content = stack;
            var dlgRes = win.ShowDialog();
            tcs.SetResult(dlgRes == true ? result : null);
        });
        return tcs.Task;
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
