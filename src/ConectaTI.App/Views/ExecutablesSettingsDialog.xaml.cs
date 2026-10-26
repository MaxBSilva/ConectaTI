using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;
using Microsoft.Win32;

namespace ConectaTI.App.Views;

public partial class ExecutablesSettingsDialog : Window
{
    private readonly SettingsService _settingsService;
    private readonly CredentialVaultService _vaultService;
    private readonly MainViewModel? _mainViewModel;
    private bool _suppressThemeEvent;
    private bool _loadingPreferences = true;

    public bool DataModified { get; private set; }
    public bool LockRequested { get; private set; }

    public ExecutablesSettingsDialog(
        SettingsService settingsService,
        CredentialVaultService vaultService,
        MainViewModel? mainViewModel = null,
        int initialTabIndex = 0)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _settingsService = settingsService;
        _vaultService = vaultService;
        _mainViewModel = mainViewModel;

        if (initialTabIndex >= 0 && initialTabIndex < SettingsTabs.Items.Count)
        {
            SettingsTabs.SelectedIndex = initialTabIndex;
        }

        LoadCurrentValues();
        _loadingPreferences = false;
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

        UpdateMasterPasswordStatus();
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
            DiagCountsText.Text = L.Format("{0} ativas | {1} na lixeira", (diag.ActiveConnectionsCount), (diag.TrashConnectionsCount));
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
        UpdateMasterPasswordStatus();
        ValidateAllExecutableBoxes();
        _ = LoadDiagnosticValuesAsync();
    }
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
    }

    private async void UpdateMasterPasswordStatus()
    {
        MasterPasswordStatusText.Text = L.Text("As senhas são protegidas automaticamente para este usuário Windows. Para compartilhar, exporte um pacote com senhas.");
        try { MigratePreviousPasswordsButton.Visibility=await _vaultService.HasPreviousVaultCredentialsAsync()
            ? Visibility.Visible : Visibility.Collapsed; }
        catch(Exception ex){_vaultService.RecordMigrationFailure(ex);}
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
        new VaultMigrationDialog(_vaultService) { Owner=this }.ShowDialog();
        UpdateMasterPasswordStatus();
    }

    private void OpenCredentialsDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        var dlg = new CredentialsDialog(_mainViewModel.Repository, _vaultService)
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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

        if (int.TryParse(ClipboardClearSecondsBox.Text, out var clipSec))
            s.ClipboardClearSeconds = Math.Clamp(clipSec, 5, 300);

        if (int.TryParse(RevealSecondsBox.Text, out var revSec))
            s.PasswordRevealSeconds = Math.Clamp(revSec, 5, 120);

        _settingsService.Save();
        ThemeService.ApplyTheme(s.Theme);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Reverte eventual prévia de tema caso o usuário cancele
        var reloaded = _settingsService.Load();
        ThemeService.ApplyTheme(reloaded.Theme);
        DialogResult = false;
        Close();
    }
}
