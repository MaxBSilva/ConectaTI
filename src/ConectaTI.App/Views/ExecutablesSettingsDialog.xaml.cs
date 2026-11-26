using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.Services.Updates;
using ConectaTI.App.ViewModels;
using Microsoft.Win32;

namespace ConectaTI.App.Views;

public partial class ExecutablesSettingsDialog : Window
{
    public const int TabGeneralIndex = 0;
    public const int TabAppearanceIndex = 1;
    public const int TabClientsIndex = 2;
    public const int TabCredentialsIndex = 3;
    public const int TabImportBackupIndex = 4;
    public const int TabShortcutsIndex = 5;
    public const int TabDiagnosticsIndex = 6;

    private readonly SettingsService _settingsService;
    private readonly CredentialVaultService _vaultService;
    private readonly MainViewModel? _mainViewModel;
    private readonly AppUpdateService _updateService;
    private readonly HotKeyTextService? _quickTextsService;
    private UpdateCheckResult? _latestUpdateCheck;
    private bool _suppressThemeEvent;
    private bool _loadingPreferences = true;

    public bool DataModified { get; private set; }
    public bool LockRequested { get; private set; }

    public ExecutablesSettingsDialog(
        SettingsService settingsService,
        CredentialVaultService vaultService,
        MainViewModel? mainViewModel = null,
        int initialTabIndex = 0,
        bool autoCheckUpdatesOnLoad = false,
        AppUpdateService? updateService = null,
        HotKeyTextService? quickTextsService = null)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _settingsService = settingsService;
        _vaultService = vaultService;
        _mainViewModel = mainViewModel;
        _updateService = updateService ?? new AppUpdateService();
        _quickTextsService = quickTextsService;

        if (initialTabIndex >= 0 && initialTabIndex < SettingsTabs.Items.Count)
        {
            SettingsTabs.SelectedIndex = initialTabIndex;
        }

        LoadCurrentValues();
        _loadingPreferences = false;

        Loaded += (_, _) =>
        {
            SyncStartWithWindowsCheckFromOwnerOrRegistry();
            RefreshShortcutsTabUI();
        };

        if (autoCheckUpdatesOnLoad)
        {
            Loaded += async (_, _) => await TriggerUpdateCheckAsync(showMessageBox: true);
        }
    }

    private HotKeyTextService? ResolveQuickTextsService()
        => _quickTextsService ?? (Owner as MainWindow)?.QuickTextsService;

    private void SyncStartWithWindowsCheckFromOwnerOrRegistry()
    {
        var qts = ResolveQuickTextsService();
        if (qts is not null)
        {
            StartWithWindowsSettingsCheck.IsChecked = qts.StartWithWindows;
        }
        else if (Application.Current is App { IsIsolatedHost: false })
        {
            StartWithWindowsSettingsCheck.IsChecked = HotKeyTextService.GetDefaultStartWithWindows();
        }
    }

    private void LoadCurrentValues()
    {
        var s = _settingsService.Current;

        LanguageCombo.SelectedIndex = (int)s.Language;
        DensityCombo.SelectedIndex = s.ComfortableMode ? 1 : 0;
        FontSizeSlider.Value = s.FontSize;
        _suppressThemeEvent = true;
        try
        {
            ThemeModeCombo.SelectedIndex = s.Theme switch
            {
                AppThemeMode.Light => 1,
                AppThemeMode.Dark => 2,
                _ => 0
            };
        }
        finally
        {
            _suppressThemeEvent = false;
        }

        MstscPathBox.Text = string.IsNullOrWhiteSpace(s.MstscExecutablePath)
            ? ExternalClientDetector.GetDefaultMstscPath()
            : s.MstscExecutablePath;
        PuttyPathBox.Text = s.PuttyExecutablePath;
        OpenSshPathBox.Text = s.OpenSshExecutablePath;
        WinScpPathBox.Text = s.WinScpExecutablePath;
        VncPathBox.Text = s.VncExecutablePath;
        VncTemplateBox.Text = s.VncCustomArgumentsTemplate;

        PreferredSshCombo.SelectedIndex = s.PreferredSshClient == SshClientPreference.OpenSsh ? 1 : 0;
        VncFlavorCombo.SelectedIndex = s.VncFlavor switch
        {
            VncViewerFlavor.TightVnc => 1,
            VncViewerFlavor.UltraVnc => 2,
            VncViewerFlavor.Custom => 3,
            _ => 0
        };

        RegisterRdpVaultCheck.IsChecked = s.RegisterRdpCredentialsInWindowsVault;
        ClipboardClearSecondsBox.Text = s.ClipboardClearSeconds.ToString();
        RevealSecondsBox.Text = s.PasswordRevealSeconds.ToString();

        OpenStartupWebsitesCheck.IsChecked = s.OpenStartupWebsites;
        StartupSite1Box.Text = s.StartupWebsite1;
        StartupSite2Box.Text = s.StartupWebsite2;
        StartupSite3Box.Text = s.StartupWebsite3;
        SyncStartWithWindowsCheckFromOwnerOrRegistry();

        RefreshShortcutsTabUI();
        UpdateMasterPasswordStatus();
        RefreshUpdateSectionState();
        ValidateAllExecutableBoxes();
        _ = LoadDiagnosticValuesAsync();
    }

    private async Task LoadDiagnosticValuesAsync()
    {
        if (_mainViewModel == null)
            return;

        try
        {
            var diag = await _mainViewModel.AppDiagnosticService.CollectDiagnosticInfoAsync();
            DiagDbPathText.Text = diag.DatabasePath;
            DiagSchemaVersionText.Text = $"v{diag.SchemaVersion}";
            DiagWritableText.Text = diag.IsDataDirectoryWritable ? L.Text("Gravável (OK)") : L.Text("Somente Leitura (Restrito)");
            DiagCountsText.Text = L.Format("{0} fora da lixeira | {1} na lixeira", diag.ConnectionsOutsideTrashCount, diag.TrashConnectionsCount);
            DiagLastErrorText.Text = string.IsNullOrWhiteSpace(diag.LastSanitizedError)
                ? L.Text("Nenhum erro registrado nesta sessão.")
                : diag.LastSanitizedError;
        }
        catch (Exception ex)
        {
            DiagDbPathText.Text = L.Text("Erro ao coletar diagnóstico");
            DiagLastErrorText.Text = ex.Message;
        }
    }

    private void OpenDatabaseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            var dir = _mainViewModel.Paths.RootDirectory;
            if (System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            var dir = _mainViewModel.Paths.LogsDirectory;
            if (System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    private async void CopyDiagnosticReport_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            var info = await _mainViewModel.AppDiagnosticService.CollectDiagnosticInfoAsync();
            var report = ConectaTI.App.Services.Diagnostics.AppDiagnosticService.FormatReport(info);
            System.Windows.Clipboard.SetText(report);
            DialogFeedbackText.Text = L.Text("Relatório de diagnóstico copiado para a área de transferência!");
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPreferences || LanguageCombo.SelectedIndex < 0) return;
        _settingsService.Current.Language = (AppLanguage)LanguageCombo.SelectedIndex;
        _settingsService.Save();
        LocalizationService.Current.SetLanguage(_settingsService.Current.Language);
        RefreshShortcutsTabUI();
        UpdateMasterPasswordStatus();
        RefreshUpdateSectionState();
        ValidateAllExecutableBoxes();
        _ = LoadDiagnosticValuesAsync();
    }
    private void ResetFontSize_Click(object sender, RoutedEventArgs e) => FontSizeSlider.Value = 12.5;
    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyViewPreferences();
    private void ViewPreference_Changed(object sender, SelectionChangedEventArgs e) => ApplyViewPreferences();
    private void ApplyViewPreferences()
    {
        if (_loadingPreferences) return;
        _settingsService.Current.ComfortableMode = DensityCombo.SelectedIndex == 1;
        _settingsService.Current.FontSize = FontSizeSlider.Value;
        ViewPreferencesService.Apply(_settingsService.Current);
        _settingsService.Save();
    }

    private void ThemeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeEvent)
            return;

        var mode = ThemeModeCombo.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };

        _settingsService.Current.Theme = mode;
        ThemeService.ApplyTheme(mode);
        _mainViewModel?.RefreshThemeProperties();
    }

    private async void UpdateMasterPasswordStatus()
    {
        MasterPasswordStatusText.Text = L.Text("As senhas são protegidas automaticamente para este usuário Windows. Para compartilhar, exporte um pacote com senhas.");
        try { MigratePreviousPasswordsButton.Visibility = await _vaultService.HasPreviousVaultCredentialsAsync()
            ? Visibility.Visible : Visibility.Collapsed; }
        catch (Exception ex) { _vaultService.RecordMigrationFailure(ex); }
    }

    private void ExecutablePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateAllExecutableBoxes();
    }

    private void ValidateAllExecutableBoxes()
    {
        if (MstscValidationText == null)
            return;

        UpdatePathStatus(MstscPathBox.Text, MstscValidationText, isRequiredForRdp: true);
        UpdatePathStatus(PuttyPathBox.Text, PuttyValidationText, isRequiredForRdp: false);
        UpdatePathStatus(OpenSshPathBox.Text, OpenSshValidationText, isRequiredForRdp: false);
        UpdatePathStatus(WinScpPathBox.Text, WinScpValidationText, isRequiredForRdp: false);
        UpdatePathStatus(VncPathBox.Text, VncValidationText, isRequiredForRdp: false);
    }

    private void UpdatePathStatus(string? rawPath, TextBlock statusBlock, bool isRequiredForRdp)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            statusBlock.Text = isRequiredForRdp ? L.Text("Caminho não informado") : L.Text("Não configurado");
            statusBlock.Foreground = (TryFindResource(isRequiredForRdp ? "WarningBrush" : "SecondaryTextBrush") as Brush) ?? Brushes.Gray;
            return;
        }

        if (ExternalClientDetector.IsExecutableValid(rawPath, out var resolved))
        {
            statusBlock.Text = L.Format("Disponível ({0})", (resolved));
            statusBlock.Foreground = (TryFindResource("SuccessBrush") as Brush) ?? Brushes.SeaGreen;
        }
        else
        {
            statusBlock.Text = L.Text("Arquivo não encontrado");
            statusBlock.Foreground = (TryFindResource("DangerBrush") as Brush) ?? Brushes.IndianRed;
        }
    }

    private void RestoreDefaultMstsc_Click(object sender, RoutedEventArgs e)
    {
        MstscPathBox.Text = ExternalClientDetector.GetDefaultMstscPath();
        ValidateAllExecutableBoxes();
        DialogFeedbackText.Text = L.Text("Caminho padrão do MSTSC restaurado.");
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ExternalClientDetector.IsExecutableValid(MstscPathBox.Text, out _))
            MstscPathBox.Text = ExternalClientDetector.GetDefaultMstscPath();

        if (!ExternalClientDetector.IsExecutableValid(PuttyPathBox.Text, out _))
            PuttyPathBox.Text = ExternalClientDetector.DetectPutty() ?? PuttyPathBox.Text;

        if (!ExternalClientDetector.IsExecutableValid(OpenSshPathBox.Text, out _))
            OpenSshPathBox.Text = ExternalClientDetector.DetectOpenSsh() ?? OpenSshPathBox.Text;

        if (!ExternalClientDetector.IsExecutableValid(WinScpPathBox.Text, out _))
            WinScpPathBox.Text = ExternalClientDetector.DetectWinScp() ?? WinScpPathBox.Text;

        if (!ExternalClientDetector.IsExecutableValid(VncPathBox.Text, out _))
        {
            var vnc = ExternalClientDetector.DetectVncViewer(out var flavor);
            if (!string.IsNullOrWhiteSpace(vnc))
            {
                VncPathBox.Text = vnc;
                VncFlavorCombo.SelectedIndex = (int)flavor;
            }
        }

        ValidateAllExecutableBoxes();
        DialogFeedbackText.Text = L.Text("Detecção automática concluída.");
    }

    private void BrowseMstsc_Click(object sender, RoutedEventArgs e) => BrowseExe(MstscPathBox, L.Text("mstsc.exe|mstsc.exe|Executáveis (*.exe)|*.exe"));
    private void BrowsePutty_Click(object sender, RoutedEventArgs e) => BrowseExe(PuttyPathBox, L.Text("putty.exe|putty.exe|Executáveis (*.exe)|*.exe"));
    private void BrowseOpenSsh_Click(object sender, RoutedEventArgs e) => BrowseExe(OpenSshPathBox, L.Text("ssh.exe|ssh.exe|Executáveis (*.exe)|*.exe"));
    private void BrowseWinScp_Click(object sender, RoutedEventArgs e) => BrowseExe(WinScpPathBox, L.Text("WinSCP.exe|WinSCP.exe|Executáveis (*.exe)|*.exe"));
    private void BrowseVnc_Click(object sender, RoutedEventArgs e) => BrowseExe(VncPathBox, "Clientes VNC (*.exe)|*.exe");

    private static void BrowseExe(TextBox targetBox, string filter)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.Text("Selecionar Executável"),
            Filter = filter,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            targetBox.Text = dlg.FileName;
        }
    }

    private void MigrateMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        new VaultMigrationDialog(_vaultService) { Owner = this }.ShowDialog();
        UpdateMasterPasswordStatus();
    }

    private void OpenCredentialsDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        var dlg = new CredentialsDialog(
            _mainViewModel.Repository,
            _vaultService,
            _mainViewModel.ClipboardService,
            _settingsService)
        {
            Owner = this
        };
        dlg.ShowDialog();
        DataModified = true;
        DialogFeedbackText.Text = L.Text("Credenciais atualizadas.");
    }

    private void OpenImportExportDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        var dlg = new ImportExportDialog(_mainViewModel.Paths, _mainViewModel.Repository, _mainViewModel.ImportExportService, _settingsService)
        {
            Owner = this
        };
        dlg.ShowDialog();
        if (dlg.DataModified)
        {
            DataModified = true;
            DialogFeedbackText.Text = L.Text("Dados atualizados pela importação/restauração.");
        }
    }

    private async void CreateQuickBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        try
        {
            var backupPath = await _mainViewModel.Repository.CreateBackupAsync();
            DialogFeedbackText.Text = L.Format("Backup criado: {0}", (System.IO.Path.GetFileName(backupPath)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Text("Erro ao Criar Backup"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenStartupWebsitesNow_Click(object sender, RoutedEventArgs e)
    {
        if (!StartupWebsitesService.ValidateSettingsInputs(
                StartupSite1Box.Text,
                StartupSite2Box.Text,
                StartupSite3Box.Text,
                out var norm1,
                out var norm2,
                out var norm3,
                out var invalidSlot))
        {
            MessageBox.Show(
                this,
                L.Format("O endereço informado no Site {0} não é uma URL HTTP/HTTPS válida.", invalidSlot),
                L.Text("Validação"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StartupSite1Box.Text = norm1;
        StartupSite2Box.Text = norm2;
        StartupSite3Box.Text = norm3;

        var previewSettings = new AppSettings
        {
            OpenStartupWebsites = true,
            StartupWebsite1 = norm1,
            StartupWebsite2 = norm2,
            StartupWebsite3 = norm3
        };

        if (StartupWebsitesService.GetConfiguredUrls(previewSettings).Count == 0)
        {
            DialogFeedbackText.Text = L.Text("Informe ao menos um site (Site 1 a 3) para abrir no navegador.");
            return;
        }

        var opened = StartupWebsitesService.LaunchConfiguredWebsites(previewSettings);
        DialogFeedbackText.Text = L.Format("{0} site(s) aberto(s) no navegador padrão.", opened);
    }

    private void RefreshShortcutsTabUI()
    {
        ShortcutsItemsControl.ItemsSource = KeyboardShortcutsCatalog.GetAll(_settingsService.Current);
        var qts = ResolveQuickTextsService();
        var triggerDisplay = qts?.EffectiveTriggerDisplay ?? HotKeyTextService.DefaultTriggerShortcut;
        SettingsQuickTextsTriggerBox.Text = triggerDisplay;
        ResetQuickTextsTriggerButton.IsEnabled = !string.Equals(
            triggerDisplay,
            HotKeyTextService.DefaultTriggerShortcut,
            StringComparison.OrdinalIgnoreCase);
    }

    private void CustomShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ResolveQuickTextsService() is { } qts)
            qts.IsCapturingShortcut = true;
    }

    private void CustomShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ResolveQuickTextsService() is { } qts)
            qts.IsCapturingShortcut = false;
    }

    private void CustomShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string actionId } || string.IsNullOrWhiteSpace(actionId))
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            return;

        e.Handled = true;
        if (key == Key.Escape)
        {
            ResetAllShortcutsButton.Focus();
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            KeyboardShortcutsCatalog.ResetActionShortcut(_settingsService.Current, actionId);
            _settingsService.Save();
            RefreshShortcutsTabUI();
            DialogFeedbackText.Text = L.Text("Atalho restaurado para o padrão.");
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotKeyTextService.EhModificador(vk))
            return;

        if (!KeyboardShortcutsCatalog.TryFormatShortcutFromKeyEvent(e, out var formatted, out var isReserved))
        {
            DialogFeedbackText.Text = isReserved
                ? L.Text("Atalhos padrão de edição (Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A, Ctrl+Z) são reservados.")
                : L.Text("Use F1–F12 ou combine a tecla com Ctrl ou Alt (ex.: Ctrl+Shift+N).");
            return;
        }

        if (!KeyboardShortcutsCatalog.TryAssignShortcut(_settingsService.Current, actionId, formatted, out var conflictName))
        {
            DialogFeedbackText.Text = L.Format("O atalho '{0}' já está em uso por '{1}'.", formatted, conflictName ?? string.Empty);
            return;
        }

        _settingsService.Save();
        RefreshShortcutsTabUI();
        DialogFeedbackText.Text = L.Format("Atalho atualizado para {0}.", formatted);
    }

    private void ResetSingleShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string actionId } || string.IsNullOrWhiteSpace(actionId))
            return;

        KeyboardShortcutsCatalog.ResetActionShortcut(_settingsService.Current, actionId);
        _settingsService.Save();
        RefreshShortcutsTabUI();
        DialogFeedbackText.Text = L.Text("Atalho restaurado para o padrão.");
    }

    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        KeyboardShortcutsCatalog.ResetAllShortcuts(_settingsService.Current);
        _settingsService.Save();
        if (ResolveQuickTextsService() is { } qts)
            qts.ResetTriggerShortcutToDefault();
        RefreshShortcutsTabUI();
        DialogFeedbackText.Text = L.Text("Todos os atalhos foram restaurados para o padrão.");
    }

    private void SettingsQuickTextsTriggerBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ResolveQuickTextsService() is { } qts)
            qts.IsCapturingTriggerShortcut = true;
    }

    private void SettingsQuickTextsTriggerBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ResolveQuickTextsService() is { } qts)
            qts.IsCapturingTriggerShortcut = false;
    }

    private void SettingsQuickTextsTriggerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            return;

        e.Handled = true;
        if (key == Key.Escape)
        {
            ResetAllShortcutsButton.Focus();
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            ApplyQuickTextsTriggerFromSettings(HotKeyTextService.DefaultTriggerShortcut);
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
            DialogFeedbackText.Text = L.Text("Use uma tecla dedicada (Scroll Lock, Pause, Insert, F1–F12) ou combine com Ctrl/Alt (ex.: Ctrl+Espaço).");
            return;
        }

        ApplyQuickTextsTriggerFromSettings(formatted);
    }

    private void ResetQuickTextsTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyQuickTextsTriggerFromSettings(HotKeyTextService.DefaultTriggerShortcut);
    }

    private void ApplyQuickTextsTriggerFromSettings(string shortcut)
    {
        if (ResolveQuickTextsService() is { } qts)
        {
            qts.SetTriggerShortcut(shortcut);
        }
        else if (Application.Current is App { IsIsolatedHost: false })
        {
            using var tempService = new HotKeyTextService(installGlobalHooks: false);
            tempService.SetTriggerShortcut(shortcut);
        }

        RefreshShortcutsTabUI();
        DialogFeedbackText.Text = L.Format("Tecla de abertura dos Textos rápidos definida para {0}.", shortcut);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!StartupWebsitesService.ValidateSettingsInputs(
                StartupSite1Box.Text,
                StartupSite2Box.Text,
                StartupSite3Box.Text,
                out var normSite1,
                out var normSite2,
                out var normSite3,
                out var invalidSiteSlot))
        {
            SettingsTabs.SelectedIndex = TabGeneralIndex;
            MessageBox.Show(
                this,
                L.Format("O endereço informado no Site {0} não é uma URL HTTP/HTTPS válida.", invalidSiteSlot),
                L.Text("Validação"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            (invalidSiteSlot switch
            {
                1 => StartupSite1Box,
                2 => StartupSite2Box,
                _ => StartupSite3Box
            }).Focus();
            return;
        }

        var mstscCandidate = MstscPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(mstscCandidate) && !ExternalClientDetector.IsExecutableValid(mstscCandidate, out var resolvedMstsc))
        {
            var confirmInvalid = MessageBox.Show(
                this,
                L.Format("O caminho informado para o MSTSC ('{0}') não foi encontrado no disco.\nDeseja salvar mesmo assim?", (resolvedMstsc)),
                L.Text("Executável MSTSC Não Encontrado"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmInvalid != MessageBoxResult.Yes)
                return;
        }

        var s = _settingsService.Current;
        s.MstscExecutablePath = mstscCandidate;
        s.PuttyExecutablePath = PuttyPathBox.Text.Trim();
        s.OpenSshExecutablePath = OpenSshPathBox.Text.Trim();
        s.WinScpExecutablePath = WinScpPathBox.Text.Trim();
        s.VncExecutablePath = VncPathBox.Text.Trim();
        s.VncCustomArgumentsTemplate = string.IsNullOrWhiteSpace(VncTemplateBox.Text) ? "{host}:{port}" : VncTemplateBox.Text.Trim();

        s.PreferredSshClient = PreferredSshCombo.SelectedIndex == 1 ? SshClientPreference.OpenSsh : SshClientPreference.Putty;
        s.VncFlavor = VncFlavorCombo.SelectedIndex switch
        {
            1 => VncViewerFlavor.TightVnc,
            2 => VncViewerFlavor.UltraVnc,
            3 => VncViewerFlavor.Custom,
            _ => VncViewerFlavor.TigerRealVnc
        };

        s.RegisterRdpCredentialsInWindowsVault = RegisterRdpVaultCheck.IsChecked == true;
        s.OpenStartupWebsites = OpenStartupWebsitesCheck.IsChecked == true;
        s.StartupWebsite1 = normSite1;
        s.StartupWebsite2 = normSite2;
        s.StartupWebsite3 = normSite3;

        var startWithWin = StartWithWindowsSettingsCheck.IsChecked == true;
        if (ResolveQuickTextsService() is { } qts)
        {
            qts.StartWithWindows = startWithWin;
        }
        else if (Application.Current is App { IsIsolatedHost: false })
        {
            HotKeyTextService.SetDefaultStartWithWindows(startWithWin);
        }

        if (int.TryParse(ClipboardClearSecondsBox.Text, out var clipSec))
            s.ClipboardClearSeconds = Math.Clamp(clipSec, 5, 300);

        if (int.TryParse(RevealSecondsBox.Text, out var revSec))
            s.PasswordRevealSeconds = Math.Clamp(revSec, 5, 120);

        _settingsService.Save();
        ThemeService.ApplyTheme(s.Theme);
        _mainViewModel?.RefreshThemeProperties();
        DialogResult = true;
        Close();
    }

    private void RefreshUpdateSectionState()
    {
        InstalledSuiteVersionBadgeText.Text = $"v{AppUpdateService.GetInstalledVersion()}";

        if (AppUpdateService.HasRollbackBackup())
        {
            var meta = AppUpdateService.TryReadRollbackMetadata();
            RestorePreviousVersionButton.Visibility = Visibility.Visible;
            RestorePreviousVersionButton.ToolTip = meta != null
                ? $"Restaurar instalação anterior (v{meta.PreviousVersion})"
                : "Restaurar instalação anterior";
        }
        else
        {
            RestorePreviousVersionButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await TriggerUpdateCheckAsync(showMessageBox: true);
    }

    public async Task<UpdateCheckResult> TriggerUpdateCheckAsync(bool showMessageBox = true)
    {
        CheckUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusTextBlock.Visibility = Visibility.Visible;
        UpdateStatusTextBlock.Text = "Consultando última Release estável em MaxBSilva/ConectaTI...";

        try
        {
            var res = await _updateService.CheckForUpdatesAsync();
            _latestUpdateCheck = res;

            if (!res.Success)
            {
                UpdateStatusTextBlock.Text = res.ErrorMessage ?? res.StatusMessage;
                if (showMessageBox)
                {
                    MessageBox.Show(
                        this,
                        res.ErrorMessage ?? res.StatusMessage,
                        L.Text("Buscar atualizações"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return res;
            }

            if (!res.IsUpdateAvailable)
            {
                var upToDateText = L.Text("Você já está na versão mais recente");
                UpdateStatusTextBlock.Text = $"{upToDateText} (v{res.InstalledVersion}).";
                ApplyUpdateButton.Visibility = Visibility.Collapsed;

                if (showMessageBox)
                {
                    MessageBox.Show(
                        this,
                        upToDateText,
                        L.Text("Buscar atualizações"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return res;
            }

            UpdateStatusTextBlock.Text =
                $"Versão instalada: v{res.InstalledVersion}  →  Disponível: v{res.AvailableVersion}";
            ApplyUpdateButton.Visibility = Visibility.Visible;

            if (showMessageBox)
            {
                var dr = MessageBox.Show(
                    this,
                    $"Nova versão disponível em MaxBSilva/ConectaTI!\n\n" +
                    $"Versão instalada: v{res.InstalledVersion}\n" +
                    $"Versão disponível: v{res.AvailableVersion}\n\n" +
                    $"Deseja atualizar agora? Seus acessos, credenciais, configurações, backups e textos rápidos serão mantidos.",
                    L.Text("Buscar atualizações"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (dr == MessageBoxResult.Yes)
                {
                    await ExecuteApplyUpdateFlowAsync(confirmWithUser: false);
                }
            }

            return res;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            RefreshUpdateSectionState();
        }
    }

    private async void ApplyUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteApplyUpdateFlowAsync(confirmWithUser: true);
    }

    private async Task ExecuteApplyUpdateFlowAsync(bool confirmWithUser)
    {
        if (_latestUpdateCheck is null || !_latestUpdateCheck.IsUpdateAvailable)
            return;

        if (_mainViewModel?.HasAnyUnsavedDrafts == true)
        {
            var draftWarn = MessageBox.Show(
                this,
                "Existem rascunhos de edição pendentes não salvos nesta sessão.\n\n" +
                "O ConectaTI só grava alterações por Salvar (Ctrl+S). Se continuar e reiniciar agora, os rascunhos em memória serão descartados.\n\n" +
                "Deseja continuar com a atualização mesmo assim?",
                "Rascunhos Pendentes Antes de Reiniciar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (draftWarn != MessageBoxResult.Yes)
            {
                DialogFeedbackText.Text = "Atualização adiada para permitir salvar os rascunhos pendentes (Ctrl+S).";
                return;
            }
        }
        else if (confirmWithUser)
        {
            var confirm = MessageBox.Show(
                this,
                $"Confirma o download, validação (SHA-256) e instalação da versão v{_latestUpdateCheck.AvailableVersion}?\n\n" +
                $"Versão instalada: v{_latestUpdateCheck.InstalledVersion}\n" +
                $"Versão disponível: v{_latestUpdateCheck.AvailableVersion}\n\n" +
                $"Banco, credenciais, configurações, backups e textos rápidos serão preservados.",
                L.Text("Atualizar agora"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        CheckUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusTextBlock.Visibility = Visibility.Visible;

        try
        {
            var result = await _updateService.DownloadValidateAndApplyAsync(
                _latestUpdateCheck,
                AppContext.BaseDirectory,
                progressCallback: msg => Dispatcher.Invoke(() => UpdateStatusTextBlock.Text = msg));

            UpdateStatusTextBlock.Text = result.Message;
            RefreshUpdateSectionState();

            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    "Falha na Atualização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApplyUpdateButton.Visibility = Visibility.Collapsed;
            var restartPrompt = MessageBox.Show(
                this,
                $"{result.Message}\n\nDeseja reiniciar o ConectaTI agora para carregar a nova versão?",
                "Atualização Concluída",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (restartPrompt == MessageBoxResult.Yes)
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "ConectaTI.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo(exePath)
                    {
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = false
                    });
                    Application.Current.Shutdown();
                }
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            ApplyUpdateButton.IsEnabled = true;
        }
    }

    private void RestorePreviousVersionButton_Click(object sender, RoutedEventArgs e)
    {
        var meta = AppUpdateService.TryReadRollbackMetadata();
        var prevVer = meta?.PreviousVersion ?? "anterior";

        if (_mainViewModel?.HasAnyUnsavedDrafts == true)
        {
            var draftWarn = MessageBox.Show(
                this,
                "Existem rascunhos não salvos em memória. Deseja continuar com a restauração da instalação anterior?",
                "Rascunhos Pendentes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (draftWarn != MessageBoxResult.Yes)
                return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Deseja restaurar a instalação anterior (v{prevVer}) do conjunto ConectaTI + HotKeyText?\n" +
            $"Seus dados, credenciais, configurações e textos rápidos permanecerão intactos.",
            L.Text("Restaurar versão anterior"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        if (AppUpdateService.RestorePreviousInstallation(AppContext.BaseDirectory, string.Empty, out var err))
        {
            UpdateStatusTextBlock.Visibility = Visibility.Visible;
            UpdateStatusTextBlock.Text = $"Instalação anterior (v{prevVer}) restaurada com sucesso.";
            RefreshUpdateSectionState();
            MessageBox.Show(
                this,
                $"A versão anterior (v{prevVer}) foi restaurada na pasta do aplicativo.",
                L.Text("Restaurar versão anterior"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                this,
                $"Não foi possível restaurar a versão anterior: {err}",
                L.Text("Restaurar versão anterior"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenQuickTextsFromSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mainWin)
        {
            mainWin.OpenQuickTextsPanel();
            DialogFeedbackText.Text = "Painel integrado de Textos rápidos aberto na janela principal.";
            Close();
        }
        else
        {
            DialogFeedbackText.Text = "Módulo integrado de Textos rápidos disponível na borda direita da janela principal.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Reverte eventual prévia de tema caso o usuário cancele
        var reloaded = _settingsService.Load();
        ThemeService.ApplyTheme(reloaded.Theme);
        _mainViewModel?.RefreshThemeProperties();
        DialogResult = false;
        Close();
    }
}
