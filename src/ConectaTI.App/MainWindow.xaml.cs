using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.Services.Updates;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;
using Microsoft.Win32;

namespace ConectaTI.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly HotKeyTextService _quickTextsService;
    private readonly DispatcherTimer _incrementalSearchTimer;
    private readonly DispatcherTimer _quickTextsSaveDebounceTimer;
    private string _incrementalSearchBuffer = string.Empty;
    private bool _isUpdatingPasswordInternally;
    private bool _isUpdatingQuickTextsInternally;
    private IInputElement? _elementFocusedBeforeQuickTexts;
    private EventWaitHandle? _updateRequestSignal;
    private CancellationTokenSource? _updateSignalCts;

    public HotKeyTextService QuickTextsService => _quickTextsService;
    public bool IsQuickTextsPanelOpen { get; private set; }
    public bool IsQuickTextsOverlayingDetails { get; private set; }

    private void ImportRdp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = L.Text("Arquivo RDP (*.rdp)|*.rdp"), Title = L.Text("Importar arquivo RDP") };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var result = RdpFileService.ImportFile(picker.FileName);
            _vm.ImportRdpDraft(result);
            MessageBox.Show(this, string.Join(Environment.NewLine, result.Warnings), L.Text("Revisar importação RDP"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception) { MessageBox.Show(this, L.Text("Não foi possível importar. Verifique o formato, endereço, codificação e opções de segurança do arquivo RDP."), L.Text("Importar RDP"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RdpDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = _vm.EditingEntry.Clone();
            var content = await _vm.BuildRdpPreviewAsync(snapshot);
            var dialog = new Window { Title = L.Text("Configuração efetiva RDP — sem segredos"), Owner = this, Width = 650, Height = 540 };
            dialog.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
            dialog.SetResourceReference(Window.ForegroundProperty, "PrimaryTextBrush");
            dialog.SetResourceReference(Window.FontSizeProperty, "AppFontSize");
            var panel = new DockPanel { Margin = new Thickness(12) };
            var export = new Button { Content = L.Text("Exportar configuração RDP"), Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(export, Dock.Bottom); panel.Children.Add(export);
            var notice = new TextBlock { Text = L.Format("Prévia do formulário atual. Salve para persistir. /admin: {0}. MSTSC iniciado não comprova sessão conectada. A saída não é assinada.", L.Text(snapshot.RdpAdminSession ? "Sim" : "Não")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) };
            DockPanel.SetDock(notice, Dock.Top); panel.Children.Add(notice);
            panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            export.Click += async (_, _) =>
            {
                var picker = new SaveFileDialog { Filter = L.Text("Arquivo RDP (*.rdp)|*.rdp"), FileName = "configuracao-efetiva.rdp" };
                if (picker.ShowDialog(dialog) != true) return;
                try { await System.IO.File.WriteAllTextAsync(picker.FileName, content, new System.Text.UnicodeEncoding(false, true)); }
                catch { MessageBox.Show(dialog, L.Text("Não foi possível exportar a configuração.")); }
            };
            dialog.Content = panel; dialog.ShowDialog();
        }
        catch { MessageBox.Show(this, L.Text("Configuração RDP inválida. Revise os campos e as propriedades avançadas.")); }
    }

    public MainWindow(MainViewModel viewModel, HotKeyTextService? quickTextsService = null)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _vm = viewModel;
        DataContext = _vm;

        if (quickTextsService is not null)
        {
            _quickTextsService = quickTextsService;
        }
        else if (Application.Current is App { IsIsolatedHost: false })
        {
            _quickTextsService = new HotKeyTextService();
        }
        else
        {
            var isolatedQuickTextsDir = System.IO.Path.Combine(_vm.Paths.RootDirectory, "HotKeyText");
            var isolatedPrefix = "Test_" + Guid.NewGuid().ToString("N")[..8] + "_";
            _quickTextsService = new HotKeyTextService(
                customDataDirectory: isolatedQuickTextsDir,
                instancePrefix: isolatedPrefix,
                customRunValueName: "ConectaTI_Test_" + Guid.NewGuid().ToString("N")[..8],
                installGlobalHooks: false);
        }

        RefreshQuickTextsTriggerUI();
        _quickTextsService.AttachMainWindow(this);
        _quickTextsService.ManageRequestedFromPopup += () =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            OpenQuickTextsPanel();
        };
        _quickTextsService.StateChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateQuickTextsConflictBanner();
            RefreshQuickTextsTriggerUI();
        }));

        _quickTextsSaveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _quickTextsSaveDebounceTimer.Tick += (_, _) =>
        {
            _quickTextsSaveDebounceTimer.Stop();
            _quickTextsService.Save();
        };

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

        _vm.PromptUnreachableVpnCallback = (entry, probe, resolvedClientName) =>
        {
            var dlg = new VpnPromptDialog(entry, probe, resolvedClientName, _vm.ClipboardService)
            {
                Owner = this
            };
            dlg.ShowDialog();
            return dlg.Decision;
        };

        _vm.OpenExecutableSettingsCallback = () => OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabClientsIndex);
        _vm.VaultService.RequestMigrationAsync = async () => await Dispatcher.InvokeAsync(() =>
        {
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            return new VaultMigrationDialog(_vm.VaultService) { Owner = owner }.ShowDialog() == true;
        });

        RestoreWindowGeometry();

        Loaded += async (_, _) =>
        {
            _vm.NotifySelectedSummaryProperties();
            SyncPasswordControls();
            UpdatePasswordPlaceholderVisibility();
            InitializeQuickTextsPanelState();
            UpdateAdaptiveHeaderAndPaneBounds(RootLayoutGrid.ActualWidth > 0 ? RootLayoutGrid.ActualWidth : Width);
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

        _vm.FilteredListUpdated += SyncListSelectionAndScroll;
        StartListeningForExternalUpdateRequests();
    }

    public void SyncListSelectionAndScroll()
    {
        if (ConnectionsListBox == null || _vm == null)
            return;

        if (_vm.SelectedConnection is not null && _vm.FilteredConnections.Contains(_vm.SelectedConnection))
        {
            Helpers.ConnectionListNavigation.SelectAndFocus(ConnectionsListBox, _vm.SelectedConnection);
        }
        else
        {
            if (ConnectionsListBox.SelectionMode != SelectionMode.Single)
                ConnectionsListBox.SelectedItems.Clear();
            ConnectionsListBox.SelectedItem = null;
            ConnectionsListBox.SelectedIndex = -1;
        }
    }

    private void StartListeningForExternalUpdateRequests()
    {
        try
        {
            _updateRequestSignal = new EventWaitHandle(false, EventResetMode.AutoReset, HotKeyTextService.ConectaTiUpdateEventName);
            _updateSignalCts = new CancellationTokenSource();
            var token = _updateSignalCts.Token;
            var signal = _updateRequestSignal;

            _ = Task.Run(() =>
            {
                WaitHandle[] handles = [signal, token.WaitHandle];
                while (!token.IsCancellationRequested)
                {
                    var index = WaitHandle.WaitAny(handles);
                    if (index == 0 && !token.IsCancellationRequested)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OpenUpdatesCheckFromExternalTrigger));
                    }
                    else
                    {
                        break;
                    }
                }
            }, token);
        }
        catch
        {
            // Ignorado se o ambiente restringir objetos nomeados
        }
    }

    public void OpenUpdatesCheckFromExternalTrigger()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabGeneralIndex, autoCheckUpdatesOnLoad: true);
    }

    private void QuickTextsCheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => OpenUpdatesCheckFromExternalTrigger();

    private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdaptiveHeaderAndPaneBounds(e.NewSize.Width);
    }

    public void UpdateAdaptiveHeaderAndPaneBounds(double availableWidth)
    {
        if (availableWidth <= 0 || _vm == null)
            return;

        if (!IsQuickTextsPanelOpen)
        {
            QuickTextsColumnDef.Width = new GridLength(0);
            QuickTextsSidePanel.Visibility = Visibility.Collapsed;
            IsQuickTextsOverlayingDetails = false;
        }
        else
        {
            QuickTextsSidePanel.Visibility = Visibility.Visible;
            if (availableWidth < 900)
            {
                // Janela estreita: abre o painel sobre os detalhes da conexão (Coluna 2), mantendo a lista (Coluna 0) acessível
                QuickTextsColumnDef.Width = new GridLength(0);
                Grid.SetColumn(QuickTextsSidePanel, 2);
                IsQuickTextsOverlayingDetails = true;
            }
            else
            {
                // Janela larga: painel de Textos rápidos na Coluna 3 ao lado dos detalhes
                var panelWidth = Math.Min(340, Math.Max(290, Math.Round(availableWidth * 0.32)));
                QuickTextsColumnDef.Width = new GridLength(panelWidth, GridUnitType.Pixel);
                Grid.SetColumn(QuickTextsSidePanel, 3);
                IsQuickTextsOverlayingDetails = false;
            }
        }

        // Garante que os painéis esquerdo e direito permaneçam sempre lado a lado e dentro dos limites utilizáveis
        var dockedQuickTextsWidth = IsQuickTextsPanelOpen && !IsQuickTextsOverlayingDetails ? QuickTextsColumnDef.Width.Value : 0;
        var maxLeftWidth = Math.Max(LeftColumnDef.MinWidth, availableWidth - RightColumnDef.MinWidth - dockedQuickTextsWidth - 34);
        if (LeftColumnDef.Width.IsAbsolute && LeftColumnDef.Width.Value > maxLeftWidth)
        {
            LeftColumnDef.Width = new GridLength(maxLeftWidth, GridUnitType.Pixel);
        }

        var fontScale = Math.Max(1.0, _vm.SettingsService.Current.FontSize / 12.5);
        var effectiveWidth = availableWidth / fontScale;

        if (effectiveWidth < 560)
        {
            ImportRdpButton.Visibility = Visibility.Collapsed;
            BatchEditButton.Visibility = Visibility.Collapsed;
            QuickTextsButton.Visibility = Visibility.Collapsed;
            HeaderSelectionBadge.Visibility = Visibility.Collapsed;
            ThemeButtonLabel.Visibility = effectiveWidth >= 320 ? Visibility.Visible : Visibility.Collapsed;
            SettingsButtonLabel.Visibility = Visibility.Collapsed;
        }
        else if (effectiveWidth < 760)
        {
            ImportRdpButton.Visibility = Visibility.Visible;
            BatchEditButton.Visibility = Visibility.Collapsed;
            QuickTextsButton.Visibility = Visibility.Visible;
            HeaderSelectionBadge.Visibility = Visibility.Collapsed;
            ThemeButtonLabel.Visibility = Visibility.Visible;
            SettingsButtonLabel.Visibility = Visibility.Visible;
        }
        else
        {
            ImportRdpButton.Visibility = Visibility.Visible;
            BatchEditButton.Visibility = Visibility.Visible;
            QuickTextsButton.Visibility = Visibility.Visible;
            HeaderSelectionBadge.Visibility = Visibility.Visible;
            ThemeButtonLabel.Visibility = Visibility.Visible;
            SettingsButtonLabel.Visibility = Visibility.Visible;
        }
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

        _vm.SettingsService.Save();
    }

    // =========================================================================
    // Atalhos Globais de Teclado (Ctrl+S, Ctrl+N, Ctrl+F, Ctrl+,, F1, F2, Esc)
    // =========================================================================

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (QuickTextShortcutBox.IsKeyboardFocusWithin || QuickTextTriggerKeyBox.IsKeyboardFocusWithin)
            return;

        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyboardShortcutsCatalog.TryMatchAction(_vm.SettingsService.Current, actualKey, Keyboard.Modifiers, out var matchedAction))
        {
            switch (matchedAction)
            {
                case KeyboardShortcutsCatalog.ActionSaveConnection:
                    e.Handled = true;
                    await _vm.SaveConnectionAsync();
                    SyncPasswordControls();
                    return;

                case KeyboardShortcutsCatalog.ActionNewConnection:
                    e.Handled = true;
                    NewConnectionButton_Click(this, new RoutedEventArgs());
                    return;

                case KeyboardShortcutsCatalog.ActionSearchConnection:
                    e.Handled = true;
                    SearchTextBox.Focus();
                    SearchTextBox.SelectAll();
                    return;

                case KeyboardShortcutsCatalog.ActionOpenSettings:
                    e.Handled = true;
                    OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabGeneralIndex);
                    return;

                case KeyboardShortcutsCatalog.ActionOpenShortcutsHelp:
                    e.Handled = true;
                    OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabShortcutsIndex);
                    return;

                case KeyboardShortcutsCatalog.ActionFocusConnectionName:
                    e.Handled = true;
                    ConnectionNameTextBox.Focus();
                    ConnectionNameTextBox.SelectAll();
                    return;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            if (MoreActionsMenu?.IsOpen == true || TopOverflowMenu?.IsOpen == true || ConnectionsListBox.ContextMenu?.IsOpen == true)
                return;

            if (IsQuickTextsPanelOpen &&
                QuickTextsSidePanel.IsKeyboardFocusWithin &&
                !QuickTextShortcutBox.IsKeyboardFocusWithin &&
                !QuickTextTriggerKeyBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                CollapseQuickTextsPanel();
                return;
            }

            // Se o foco estiver na lista e houver busca incremental ativa, deixa o ListBox limpar a busca incremental
            if (ConnectionsListBox.IsKeyboardFocusWithin && _incrementalSearchBuffer.Length > 0)
                return;

            if (SearchTextBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(_vm.SearchText))
                {
                    _vm.SearchText = string.Empty;
                }
                else if (ConnectionsListBox.Items.Count > 0)
                {
                    var target = _vm.SelectedConnection ?? ConnectionsListBox.Items[0];
                    Helpers.ConnectionListNavigation.SelectAndFocus(ConnectionsListBox, target);
                    if (ConnectionsListBox.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem item)
                        item.Focus();
                }
            }
            else if (RevealedPasswordTextBox.IsKeyboardFocusWithin && _vm.IsPasswordRevealed)
            {
                e.Handled = true;
                _vm.HideRevealedPassword();
                SyncPasswordControls();
                InlinePasswordBox.Focus();
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

        if (e.Key is Key.Down or Key.Up && Keyboard.Modifiers == ModifierKeys.None)
        {
            // One movement in the displayed view, never a second native movement from stale focus.
            e.Handled = true;
            ResetIncrementalSearch();
            Helpers.ConnectionListNavigation.Move(ConnectionsListBox, e.Key == Key.Down ? 1 : -1);
            return;
        }

        if (e.Key is Key.Down or Key.Up or Key.Home or Key.End or Key.PageDown or Key.PageUp)
            ResetIncrementalSearch(); // Modified navigation retains WPF's extended-selection semantics.

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
        var match = ConectaTI.App.Helpers.StringNormalizationHelper.FindBestMatch(ConnectionsListBox.Items.OfType<ConnectionEntry>(), term);
        if (match != null)
        {
            Helpers.ConnectionListNavigation.SelectAndFocus(ConnectionsListBox, match);
        }
    }

    private void ResetIncrementalSearch()
    {
        _incrementalSearchTimer.Stop();
        _incrementalSearchBuffer = string.Empty;
        _vm.IncrementalSearchStatus = string.Empty;
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
    // Menus de Ações (Barra Superior e Painel Direito)
    // =========================================================================

    private void TopOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (TopOverflowButton.ContextMenu != null)
        {
            TopOverflowButton.ContextMenu.PlacementTarget = TopOverflowButton;
            TopOverflowButton.ContextMenu.Placement = PlacementMode.Bottom;
            TopOverflowButton.ContextMenu.DataContext = DataContext;
            TopOverflowButton.ContextMenu.IsOpen = true;
        }
    }

    private async void OpenImportExportFromHeader_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ImportExportDialog(_vm.Paths, _vm.Repository, _vm.ImportExportService, _vm.SettingsService)
        {
            Owner = this
        };
        dlg.ShowDialog();
        if (dlg.DataModified)
        {
            await _vm.ReloadAllDataAsync(_vm.SelectedConnection?.Id);
        }
    }

    private async void OpenCredentialsFromHeader_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CredentialsDialog(_vm.Repository, _vm.VaultService, _vm.ClipboardService, _vm.SettingsService)
        {
            Owner = this
        };
        dlg.ShowDialog();
        await _vm.ReloadAllDataAsync(_vm.SelectedConnection?.Id);
    }

    private void OpenShortcutsTab_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabShortcutsIndex);
    }

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
    private void ConnectionsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ResetIncrementalSearch();
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

    private async void MenuOpenVpn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.OpenVpnClientAsync();
    }

    private void VpnProfilePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = VpnProfilePresetButton,
            Placement = PlacementMode.Bottom
        };

        var profiles = _vm.VpnLauncherService.DiscoverInstalledVpnProfiles();
        if (profiles.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = L.Text("Nenhum perfil local (.ovpn, FortiClient, OpenConnect ou Windows) detectado"),
                IsEnabled = false
            });
        }
        else
        {
            foreach (var profile in profiles)
            {
                var captured = profile;
                var item = new MenuItem { Header = captured.MenuHeader };
                item.Click += (_, _) => ApplyDiscoveredVpnProfile(captured);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var browseOvpnItem = new MenuItem { Header = L.Text("Procurar arquivo .ovpn...") };
        browseOvpnItem.Click += (_, _) => BrowseOvpnProfileFile();
        menu.Items.Add(browseOvpnItem);

        menu.IsOpen = true;
    }

    private void ApplyDiscoveredVpnProfile(DiscoveredVpnProfile profile)
    {
        _vm.EditingEntry.VpnName = profile.ProfileName;
        _vm.EditingEntry.VpnApp = profile.SuggestedApp;
        if (string.IsNullOrWhiteSpace(_vm.EditingEntry.VpnServer) && !string.IsNullOrWhiteSpace(profile.SuggestedServer))
        {
            _vm.EditingEntry.VpnServer = profile.SuggestedServer;
        }
        VpnNameTextBox.Focus();
        VpnNameTextBox.CaretIndex = VpnNameTextBox.Text.Length;
    }

    private void BrowseOvpnProfileFile()
    {
        var initialDir = Directory.Exists(@"C:\Program Files\OpenVPN\config")
            ? @"C:\Program Files\OpenVPN\config"
            : null;

        var dlg = new OpenFileDialog
        {
            Title = L.Text("Selecionar Perfil OpenVPN (.ovpn)"),
            Filter = L.Text("Perfis OpenVPN (*.ovpn)|*.ovpn|Todos os arquivos (*.*)|*.*"),
            CheckFileExists = true,
            InitialDirectory = initialDir ?? string.Empty
        };

        if (dlg.ShowDialog(this) == true)
        {
            ApplySelectedOvpnFile(dlg.FileName);
        }
    }

    private void ApplySelectedOvpnFile(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        _vm.EditingEntry.VpnName = baseName;

        var parentDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var isStandardOpenVpnFolder = parentDir.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase);
        _vm.EditingEntry.VpnApp = isStandardOpenVpnFolder ? "OpenVPN GUI" : filePath;

        if (string.IsNullOrWhiteSpace(_vm.EditingEntry.VpnServer))
        {
            var remote = VpnClientLauncherService.TryExtractRemoteServerFromOvpnFile(filePath);
            if (!string.IsNullOrWhiteSpace(remote))
                _vm.EditingEntry.VpnServer = remote;
        }
    }

    private void VpnAppPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = VpnAppPresetButton,
            Placement = PlacementMode.Bottom
        };

        foreach (var preset in VpnClientLauncherService.CommonVpnClientPresets)
        {
            var label = preset == "VPN do Windows" ? L.Text("VPN do Windows") : preset;
            var item = new MenuItem { Header = label };
            var captured = preset;
            item.Click += (_, _) =>
            {
                _vm.EditingEntry.VpnApp = captured;
                VpnAppTextBox.Focus();
                VpnAppTextBox.CaretIndex = VpnAppTextBox.Text.Length;
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void BrowseVpnApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.Text("Selecionar Cliente ou Perfil VPN"),
            Filter = L.Text("Clientes e Perfis VPN (*.exe;*.lnk;*.ovpn)|*.exe;*.lnk;*.ovpn|Todos os arquivos (*.*)|*.*"),
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            if (dlg.FileName.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase))
            {
                ApplySelectedOvpnFile(dlg.FileName);
            }
            else
            {
                _vm.EditingEntry.VpnApp = dlg.FileName;
            }
        }
    }

    // =========================================================================
    // Painel Integrado de Textos Rápidos (HotKeyText in-process)
    // =========================================================================

    private void RefreshQuickTextsTriggerUI()
    {
        if (_quickTextsService == null)
            return;

        var triggerDisplay = _quickTextsService.EffectiveTriggerDisplay;
        QuickTextsSubheaderText.Text = L.Format("{0} - versão {1}", triggerDisplay, AppUpdateService.GetInstalledVersion());
        QuickTextTriggerKeyBox.Text = triggerDisplay;
        QuickTextDefaultTriggerButton.IsEnabled = !string.Equals(
            triggerDisplay,
            HotKeyTextService.DefaultTriggerShortcut,
            StringComparison.OrdinalIgnoreCase);
    }

    private void InitializeQuickTextsPanelState()
    {
        var previous = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            _quickTextsService.ReindexItems();
            RefreshQuickTextsTriggerUI();
            QuickTextModeTypeRadio.IsChecked = !_quickTextsService.Config.ModoColar;
            QuickTextModePasteRadio.IsChecked = _quickTextsService.Config.ModoColar;
            QuickTextStartWithWindowsCheckBox.IsChecked = _quickTextsService.StartWithWindows;
            UpdateQuickTextsConflictBanner();
            ReloadQuickTextsList(_quickTextsService.Config.Textos.Count > 0 ? 0 : -1);
        }
        finally
        {
            _isUpdatingQuickTextsInternally = previous;
        }
    }

    private void UpdateQuickTextsConflictBanner()
    {
        if (_quickTextsService.HasStandaloneConflict)
        {
            QuickTextsConflictBanner.Visibility = Visibility.Visible;
            QuickTextsConflictTextBlock.Text = _quickTextsService.ConflictMessage;
        }
        else
        {
            QuickTextsConflictBanner.Visibility = Visibility.Collapsed;
            QuickTextsConflictTextBlock.Text = string.Empty;
        }
    }

    public void OpenQuickTextsPanel()
    {
        if (!IsQuickTextsPanelOpen)
        {
            if (Keyboard.FocusedElement is IInputElement focused &&
                !QuickTextsSidePanel.IsKeyboardFocusWithin &&
                !QuickTextsEdgeTabBorder.IsKeyboardFocusWithin &&
                !ReferenceEquals(focused, QuickTextsButton))
            {
                _elementFocusedBeforeQuickTexts = focused;
            }
        }

        _quickTextsService.CaptureCurrentForegroundAsTargetIfExternal();
        IsQuickTextsPanelOpen = true;
        QuickTextsEdgeTabArrow.Text = "▸";

        InitializeQuickTextsPanelState();
        UpdateAdaptiveHeaderAndPaneBounds(RootLayoutGrid.ActualWidth > 0 ? RootLayoutGrid.ActualWidth : Width);

        if (_quickTextsService.HasStandaloneConflict)
        {
            _vm.StatusMessage = _quickTextsService.ConflictMessage;
        }
        else
        {
            _vm.StatusMessage = L.Format("Painel de Textos rápidos aberto ({0} e atalhos ativos no processo do ConectaTI).", _quickTextsService.EffectiveTriggerDisplay);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (QuickTextsListBox.Items.Count > 0)
                QuickTextsListBox.Focus();
            else
                QuickTextsNewButton.Focus();
        }));
    }

    public void CollapseQuickTextsPanel()
    {
        if (!IsQuickTextsPanelOpen)
            return;

        _quickTextsSaveDebounceTimer.Stop();
        _quickTextsService.IsCapturingShortcut = false;
        _quickTextsService.IsCapturingTriggerShortcut = false;
        _quickTextsService.Save();

        IsQuickTextsPanelOpen = false;
        QuickTextsEdgeTabArrow.Text = "◂";
        UpdateAdaptiveHeaderAndPaneBounds(RootLayoutGrid.ActualWidth > 0 ? RootLayoutGrid.ActualWidth : Width);

        _vm.StatusMessage = _quickTextsService.HasStandaloneConflict
            ? _quickTextsService.ConflictMessage
            : L.Format("Painel de Textos rápidos recolhido ({0} e atalhos continuam ativos).", _quickTextsService.EffectiveTriggerDisplay);

        var targetFocus = _elementFocusedBeforeQuickTexts;
        if (targetFocus is UIElement { IsVisible: true, IsEnabled: true } uiTarget)
        {
            uiTarget.Focus();
        }
        else if (ConnectionNameTextBox.IsVisible && ConnectionNameTextBox.IsEnabled)
        {
            ConnectionNameTextBox.Focus();
        }
        else
        {
            SearchTextBox.Focus();
        }
    }

    public void ToggleQuickTextsPanel()
    {
        if (IsQuickTextsPanelOpen)
            CollapseQuickTextsPanel();
        else
            OpenQuickTextsPanel();
    }

    private void QuickTextsButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleQuickTextsPanel();
    }

    private void QuickTextsEdgeTabButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleQuickTextsPanel();
    }

    private void QuickTextsCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseQuickTextsPanel();
    }

    private void QuickTextsRetryActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_quickTextsService.TryActivateShortcuts())
        {
            UpdateQuickTextsConflictBanner();
            _vm.StatusMessage = "Atalhos integrados de Textos rápidos (Scroll Lock) ativados com sucesso.";
        }
        else
        {
            UpdateQuickTextsConflictBanner();
            MessageBox.Show(
                this,
                _quickTextsService.ConflictMessage,
                L.Text("Textos rápidos"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ReloadQuickTextsList(int selectIndex)
    {
        var previous = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            _quickTextsService.ReindexItems();
            QuickTextsListBox.ItemsSource = null;
            QuickTextsListBox.ItemsSource = _quickTextsService.Config.Textos;

            if (_quickTextsService.Config.Textos.Count == 0)
            {
                QuickTextsListBox.SelectedIndex = -1;
            }
            else
            {
                var clamped = Math.Clamp(selectIndex, 0, _quickTextsService.Config.Textos.Count - 1);
                QuickTextsListBox.SelectedIndex = clamped;
            }
        }
        finally
        {
            _isUpdatingQuickTextsInternally = previous;
        }

        SyncSelectedQuickTextEditorFields();
    }

    private void SyncSelectedQuickTextEditorFields()
    {
        var previous = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            var idx = QuickTextsListBox.SelectedIndex;
            var count = _quickTextsService.Config.Textos.Count;
            var hasSelection = idx >= 0 && idx < count;

            QuickTextTitleBox.IsEnabled = hasSelection;
            QuickTextShortcutBox.IsEnabled = hasSelection;
            QuickTextDefaultShortcutButton.IsEnabled = hasSelection;
            QuickTextShowContentCheckBox.IsEnabled = hasSelection;
            QuickTextContentBox.IsEnabled = hasSelection;
            QuickTextMaskedContentBox.IsEnabled = hasSelection;
            QuickTextsDeleteButton.IsEnabled = hasSelection;
            QuickTextsMoveUpButton.IsEnabled = hasSelection && idx > 0;
            QuickTextsMoveDownButton.IsEnabled = hasSelection && idx < count - 1;
            QuickTextsInsertNowButton.IsEnabled = hasSelection;

            if (!hasSelection)
            {
                QuickTextTitleBox.Text = string.Empty;
                QuickTextShortcutBox.Text = string.Empty;
                QuickTextShowContentCheckBox.IsChecked = true;
                QuickTextContentBox.Text = string.Empty;
                QuickTextMaskedContentBox.Password = string.Empty;
                QuickTextContentBox.Visibility = Visibility.Visible;
                QuickTextMaskedContentBox.Visibility = Visibility.Collapsed;
                return;
            }

            var item = _quickTextsService.Config.Textos[idx];
            QuickTextTitleBox.Text = item.Titulo;
            QuickTextShortcutBox.Text = _quickTextsService.ObterAtalhoItem(idx);
            QuickTextShowContentCheckBox.IsChecked = !item.OcultarTexto;
            QuickTextContentBox.Text = item.Conteudo;
            QuickTextMaskedContentBox.Password = item.Conteudo;

            QuickTextContentBox.Visibility = item.OcultarTexto ? Visibility.Collapsed : Visibility.Visible;
            QuickTextMaskedContentBox.Visibility = item.OcultarTexto ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingQuickTextsInternally = previous;
        }
    }

    private void QuickTextsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally)
            return;
        SyncSelectedQuickTextEditorFields();
    }

    private void QuickTextsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QuickTextsListBox.SelectedIndex >= 0)
        {
            _quickTextsService.SelectAndInsert(QuickTextsListBox.SelectedIndex, _quickTextsService.LastExternalTargetWindow);
        }
    }

    private void QuickTextsNewButton_Click(object sender, RoutedEventArgs e)
    {
        var novo = new TextoItem
        {
            Titulo = "Novo texto",
            Conteudo = string.Empty,
            Atalho = string.Empty,
            OcultarTexto = false
        };
        _quickTextsService.Config.Textos.Add(novo);
        _quickTextsService.Save();
        ReloadQuickTextsList(_quickTextsService.Config.Textos.Count - 1);
        QuickTextTitleBox.Focus();
        QuickTextTitleBox.SelectAll();
    }

    private void QuickTextsDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        _quickTextsService.Config.Textos.RemoveAt(idx);
        _quickTextsService.Save();
        ReloadQuickTextsList(Math.Min(idx, _quickTextsService.Config.Textos.Count - 1));
    }

    private void QuickTextsMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveQuickTextItem(-1);
    }

    private void QuickTextsMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveQuickTextItem(1);
    }

    private void MoveQuickTextItem(int delta)
    {
        var i = QuickTextsListBox.SelectedIndex;
        var j = i + delta;
        var list = _quickTextsService.Config.Textos;
        if (i < 0 || j < 0 || j >= list.Count)
            return;

        (list[i], list[j]) = (list[j], list[i]);
        _quickTextsService.Save();
        ReloadQuickTextsList(j);
    }

    private void QuickTextsInsertNowButton_Click(object sender, RoutedEventArgs e)
    {
        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        _quickTextsService.SelectAndInsert(idx, _quickTextsService.LastExternalTargetWindow);
    }

    private void QuickTextTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally)
            return;

        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        _quickTextsService.Config.Textos[idx].Titulo = QuickTextTitleBox.Text;
        ScheduleQuickTextsSave();
    }

    private void QuickTextContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally)
            return;

        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        var previous = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            _quickTextsService.Config.Textos[idx].Conteudo = QuickTextContentBox.Text;
            if (QuickTextMaskedContentBox.Password != QuickTextContentBox.Text)
                QuickTextMaskedContentBox.Password = QuickTextContentBox.Text;
        }
        finally
        {
            _isUpdatingQuickTextsInternally = previous;
        }

        ScheduleQuickTextsSave();
    }

    private void QuickTextMaskedContentBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally)
            return;

        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        var previous = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            _quickTextsService.Config.Textos[idx].Conteudo = QuickTextMaskedContentBox.Password;
            if (QuickTextContentBox.Text != QuickTextMaskedContentBox.Password)
                QuickTextContentBox.Text = QuickTextMaskedContentBox.Password;
        }
        finally
        {
            _isUpdatingQuickTextsInternally = previous;
        }

        ScheduleQuickTextsSave();
    }

    private void QuickTextShowContentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally)
            return;

        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        var showContent = QuickTextShowContentCheckBox.IsChecked == true;
        var item = _quickTextsService.Config.Textos[idx];
        item.OcultarTexto = !showContent;
        QuickTextContentBox.Visibility = item.OcultarTexto ? Visibility.Collapsed : Visibility.Visible;
        QuickTextMaskedContentBox.Visibility = item.OcultarTexto ? Visibility.Visible : Visibility.Collapsed;
        _quickTextsService.Save();
    }

    private void QuickTextShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _quickTextsService.IsCapturingShortcut = true;
    }

    private void QuickTextShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _quickTextsService.IsCapturingShortcut = false;
    }

    private void QuickTextShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            return; // Permite navegação por Tab

        e.Handled = true;
        if (key is Key.Back or Key.Delete)
        {
            _quickTextsService.AssignShortcut(idx, string.Empty);
            QuickTextShortcutBox.Text = _quickTextsService.ObterAtalhoItem(idx);
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotKeyTextService.EhModificador(vk) ||
            vk is 0x1B or 0x0D ||
            (vk == _quickTextsService.TriggerVirtualKey && !_quickTextsService.TriggerRequiresModifiers))
            return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var combo = HotKeyTextService.FormatarCombinacao(ctrl, alt, shift, vk);
        if (string.IsNullOrEmpty(combo))
            return;

        _quickTextsService.AssignShortcut(idx, combo);
        QuickTextShortcutBox.Text = combo;
    }

    private void QuickTextDefaultShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        var idx = QuickTextsListBox.SelectedIndex;
        if (idx < 0 || idx >= _quickTextsService.Config.Textos.Count)
            return;

        _quickTextsService.AssignShortcut(idx, string.Empty);
        QuickTextShortcutBox.Text = _quickTextsService.ObterAtalhoItem(idx);
    }

    private void QuickTextTriggerKeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _quickTextsService.IsCapturingTriggerShortcut = true;
    }

    private void QuickTextTriggerKeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _quickTextsService.IsCapturingTriggerShortcut = false;
    }

    private void QuickTextTriggerKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            return;

        e.Handled = true;
        if (key == Key.Escape)
        {
            QuickTextsListBox.Focus();
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _quickTextsService.ResetTriggerShortcutToDefault();
            RefreshQuickTextsTriggerUI();
            _vm.StatusMessage = L.Format("Tecla de abertura dos Textos rápidos definida para {0}.", _quickTextsService.EffectiveTriggerDisplay);
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotKeyTextService.EhModificador(vk))
            return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (!HotKeyTextService.TryFormatTriggerShortcut(ctrl, alt, shift, vk, out var formatted))
        {
            _vm.StatusMessage = L.Text("Use uma tecla dedicada (Scroll Lock, Pause, Insert, F1–F12) ou combine com Ctrl/Alt (ex.: Ctrl+Espaço).");
            return;
        }

        if (_quickTextsService.SetTriggerShortcut(formatted))
        {
            RefreshQuickTextsTriggerUI();
            _vm.StatusMessage = L.Format("Tecla de abertura dos Textos rápidos definida para {0}.", _quickTextsService.EffectiveTriggerDisplay);
        }
    }

    private void QuickTextDefaultTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        _quickTextsService.ResetTriggerShortcutToDefault();
        RefreshQuickTextsTriggerUI();
        _vm.StatusMessage = L.Format("Tecla de abertura dos Textos rápidos definida para {0}.", _quickTextsService.EffectiveTriggerDisplay);
    }

    private void QuickTextModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally || _quickTextsService == null)
            return;

        _quickTextsService.Config.ModoColar = QuickTextModePasteRadio.IsChecked == true;
        _quickTextsService.Save();
    }

    private void QuickTextStartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingQuickTextsInternally || _quickTextsService == null)
            return;

        _quickTextsService.StartWithWindows = QuickTextStartWithWindowsCheckBox.IsChecked == true;
    }

    private void ScheduleQuickTextsSave()
    {
        _quickTextsSaveDebounceTimer.Stop();
        _quickTextsSaveDebounceTimer.Start();
    }

    // =========================================================================
    // Janela Centralizada de Configurações (Engrenagem)
    // =========================================================================

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsDialog(initialTabIndex: ExecutablesSettingsDialog.TabGeneralIndex);
    }

    private async void OpenSettingsDialog(int initialTabIndex, bool autoCheckUpdatesOnLoad = false)
    {
        var dlg = new ExecutablesSettingsDialog(
            _vm.SettingsService,
            _vm.VaultService,
            _vm,
            initialTabIndex,
            autoCheckUpdatesOnLoad,
            quickTextsService: _quickTextsService)
        {
            Owner = this
        };
        dlg.ShowDialog();

        var prevInternal = _isUpdatingQuickTextsInternally;
        _isUpdatingQuickTextsInternally = true;
        try
        {
            QuickTextStartWithWindowsCheckBox.IsChecked = _quickTextsService.StartWithWindows;
            RefreshQuickTextsTriggerUI();
        }
        finally
        {
            _isUpdatingQuickTextsInternally = prevInternal;
        }

        UpdateAdaptiveHeaderAndPaneBounds(RootLayoutGrid.ActualWidth > 0 ? RootLayoutGrid.ActualWidth : Width);

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
        try
        {
            _quickTextsSaveDebounceTimer?.Stop();
            _quickTextsService?.Save();
            _quickTextsService?.Dispose();
        }
        catch { }

        try
        {
            _updateSignalCts?.Cancel();
            _updateSignalCts?.Dispose();
            _updateRequestSignal?.Dispose();
        }
        catch { }

        SaveWindowGeometry();
        _vm.ClearSessionDrafts();
        _vm.Paths.CleanupTempRdpFiles();
    }
}
