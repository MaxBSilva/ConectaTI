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

    public bool DataModified { get; private set; }
    public bool LockRequested { get; private set; }

    public ExecutablesSettingsDialog(
        SettingsService settingsService,
        CredentialVaultService vaultService,
        MainViewModel? mainViewModel = null,
        int initialTabIndex = 0)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _vaultService = vaultService;
        _mainViewModel = mainViewModel;

        if (initialTabIndex >= 0 && initialTabIndex < SettingsTabs.Items.Count)
        {
            SettingsTabs.SelectedIndex = initialTabIndex;
        }

        LoadCurrentValues();
    }

    private void LoadCurrentValues()
    {
        var s = _settingsService.Current;

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
            DiagWritableText.Text = diag.IsDataDirectoryWritable ? "Gravável (OK)" : "Somente Leitura (Restrito)";
            DiagCountsText.Text = $"{diag.ActiveConnectionsCount} ativas | {diag.TrashConnectionsCount} na lixeira";
            DiagLastErrorText.Text = string.IsNullOrWhiteSpace(diag.LastSanitizedError)
                ? "Nenhum erro registrado nesta sessão."
                : diag.LastSanitizedError;
        }
        catch (Exception ex)
        {
            DiagDbPathText.Text = "Erro ao coletar diagnóstico";
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
            DialogFeedbackText.Text = "Relatório de diagnóstico copiado para a área de transferência!";
        }
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

    private void UpdateMasterPasswordStatus()
    {
        var hasMaster = _settingsService.Current.HasMasterPassword;
        MasterPasswordStatusText.Text = hasMaster
            ? "Existem dados protegidos por senha mestra legada."
            : "Protegido pela conta do usuário no Windows (DPAPI). Sem bloqueio por senha mestra.";
        MigrationPanel.Visibility = hasMaster ? Visibility.Visible : Visibility.Collapsed;
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
            statusBlock.Text = isRequiredForRdp ? "Caminho não informado" : "Não configurado";
            statusBlock.Foreground = (TryFindResource(isRequiredForRdp ? "WarningBrush" : "SecondaryTextBrush") as Brush) ?? Brushes.Gray;
            return;
        }

        if (ExternalClientDetector.IsExecutableValid(rawPath, out var resolved))
        {
            statusBlock.Text = $"Disponível ({resolved})";
            statusBlock.Foreground = (TryFindResource("SuccessBrush") as Brush) ?? Brushes.SeaGreen;
        }
        else
        {
            statusBlock.Text = "Arquivo não encontrado";
            statusBlock.Foreground = (TryFindResource("DangerBrush") as Brush) ?? Brushes.IndianRed;
        }
    }

    private void RestoreDefaultMstsc_Click(object sender, RoutedEventArgs e)
    {
        MstscPathBox.Text = ExternalClientDetector.GetDefaultMstscPath();
        ValidateAllExecutableBoxes();
        DialogFeedbackText.Text = "Caminho padrão do MSTSC restaurado.";
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
        DialogFeedbackText.Text = "Detecção automática concluída.";
    }

    private void BrowseMstsc_Click(object sender, RoutedEventArgs e) => BrowseExe(MstscPathBox, "mstsc.exe|mstsc.exe|Executáveis (*.exe)|*.exe");
    private void BrowsePutty_Click(object sender, RoutedEventArgs e) => BrowseExe(PuttyPathBox, "putty.exe|putty.exe|Executáveis (*.exe)|*.exe");
    private void BrowseOpenSsh_Click(object sender, RoutedEventArgs e) => BrowseExe(OpenSshPathBox, "ssh.exe|ssh.exe|Executáveis (*.exe)|*.exe");
    private void BrowseWinScp_Click(object sender, RoutedEventArgs e) => BrowseExe(WinScpPathBox, "WinSCP.exe|WinSCP.exe|Executáveis (*.exe)|*.exe");
    private void BrowseVnc_Click(object sender, RoutedEventArgs e) => BrowseExe(VncPathBox, "Clientes VNC (*.exe)|*.exe");

    private static void BrowseExe(TextBox targetBox, string filter)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar Executável",
            Filter = filter,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            targetBox.Text = dlg.FileName;
        }
    }

    private async void MigrateMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _vaultService.MigrateFromMasterPasswordAsync(CurrentMasterPassBox.Password);
            if (ok)
            {
                CurrentMasterPassBox.Clear();
                UpdateMasterPasswordStatus();
                DataModified = true;
                DialogFeedbackText.Text = "Credenciais migradas para DPAPI com sucesso.";
                MessageBox.Show(this, "Todas as credenciais foram migradas para proteção nativa DPAPI com sucesso.\nA senha mestra foi desativada.", "Migração Concluída", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "A senha mestra informada está incorreta.", "Senha Mestra Inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro na Migração", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        DialogFeedbackText.Text = "Credenciais atualizadas.";
    }

    private void OpenImportExportDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        var dlg = new ImportExportDialog(_mainViewModel.Paths, _mainViewModel.Repository, _mainViewModel.ImportExportService)
        {
            Owner = this
        };
        dlg.ShowDialog();
        if (dlg.DataModified)
        {
            DataModified = true;
            DialogFeedbackText.Text = "Dados atualizados pela importação/restauração.";
        }
    }

    private async void CreateQuickBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        try
        {
            var backupPath = await _mainViewModel.Repository.CreateBackupAsync();
            DialogFeedbackText.Text = $"Backup criado: {System.IO.Path.GetFileName(backupPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Criar Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var mstscCandidate = MstscPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(mstscCandidate) && !ExternalClientDetector.IsExecutableValid(mstscCandidate, out var resolvedMstsc))
        {
            var confirmInvalid = MessageBox.Show(
                this,
                $"O caminho informado para o MSTSC ('{resolvedMstsc}') não foi encontrado no disco.\nDeseja salvar mesmo assim?",
                "Executável MSTSC Não Encontrado",
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
