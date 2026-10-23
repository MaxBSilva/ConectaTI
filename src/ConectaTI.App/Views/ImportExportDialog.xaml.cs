using System.Windows;
using System.Windows.Controls;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using Microsoft.Win32;

namespace ConectaTI.App.Views;

public partial class ImportExportDialog : Window
{
    private readonly AppPaths _paths;
    private readonly SqliteRepository _repository;
    private readonly ImportExportService _importExportService;

    private ParsedCsvDocument? _currentCsvDoc;
    private ImportPreviewResult? _currentPreview;
    private List<ConnectionEntry> _existingConnections = new();
    private bool _suppressMappingChange;

    public bool DataModified { get; private set; }

    public ImportExportDialog(
        AppPaths paths,
        SqliteRepository repository,
        ImportExportService importExportService)
    {
        InitializeComponent();
        _paths = paths;
        _repository = repository;
        _importExportService = importExportService;

        BackupsFolderInfoText.Text = $"Pasta padrão de backups automáticos: {_paths.BackupsDirectory}\n" +
                                     "A restauração verifica a integridade do arquivo SQLite (PRAGMA integrity_check) e gera um backup de segurança antes de substituir os dados.";

        Loaded += async (_, _) =>
        {
            _existingConnections = await _repository.GetAllConnectionsAsync();
        };
    }

    private async void SelectImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar Arquivo para Importação (XML, CSV, JSON, AutoPuTTY)",
            Filter = "Todos os Formatos Suportados (*.xml;*.csv;*.json;*.ctienc)|*.xml;*.csv;*.json;*.ctienc|Arquivos XML (*.xml)|*.xml|Arquivos CSV (*.csv)|*.csv|Arquivos JSON (*.json)|*.json|Pacote Criptografado (*.ctienc)|*.ctienc|Todos os arquivos (*.*)|*.*"
        };

        if (dlg.ShowDialog() != true)
            return;

        _existingConnections = await _repository.GetAllConnectionsAsync();
        var filePath = dlg.FileName;
        SelectedFileText.Text = filePath;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            if (ext == ".csv")
            {
                _currentCsvDoc = _importExportService.ParseCsvFile(filePath);
                PopulateCsvMappingCombos(_currentCsvDoc);
                CsvMappingPanel.Visibility = Visibility.Visible;
                RebuildCsvPreviewFromCombos();
            }
            else if (ext == ".json")
            {
                _currentCsvDoc = null;
                CsvMappingPanel.Visibility = Visibility.Collapsed;
                _currentPreview = _importExportService.ParseJsonPreview(filePath, _existingConnections);
                BindPreview(_currentPreview);
            }
            else if (ext == ".xml")
            {
                _currentCsvDoc = null;
                CsvMappingPanel.Visibility = Visibility.Collapsed;

                var rawText = File.ReadAllText(filePath);
                string? pwd = null;
                if (AutoPuttyCodec.RequiresPassword(rawText))
                {
                    pwd = PromptForXmlPassword();
                }

                _currentPreview = _importExportService.ParseXmlPreview(filePath, _existingConnections, pwd);
                BindPreview(_currentPreview);
            }
            else
            {
                _currentCsvDoc = null;
                CsvMappingPanel.Visibility = Visibility.Collapsed;
                _currentPreview = _importExportService.EvaluateAndParseAutoPuttyFile(filePath, _existingConnections);
                BindPreview(_currentPreview);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Erro ao analisar arquivo: {ex.Message}", "Falha na Leitura", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptForXmlPassword()
    {
        var win = new Window
        {
            Title = "Senha do Arquivo AutoPuTTY",
            Width = 380,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "O arquivo está protegido por senha mestra. Digite a senha para decodificar as conexões:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 12
        });

        var pwdBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
        stack.Children.Add(pwdBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "OK", Width = 75, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancelar", Width = 75, Padding = new Thickness(0, 4, 0, 4), IsCancel = true };

        string? result = null;
        okBtn.Click += (_, _) => { result = pwdBox.Password; win.DialogResult = true; win.Close(); };
        cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);

        win.Content = stack;
        var dialogRes = win.ShowDialog();
        return dialogRes == true ? result : null;
    }

    private async void ExportXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exportar Cadastros para XML (ConectaTI)",
            Filter = "Arquivo XML (*.xml)|*.xml",
            FileName = $"ConectaTI-Export-{DateTime.Now:yyyyMMdd-HHmm}.xml"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var conns = await _repository.GetAllConnectionsAsync();
            await _importExportService.ExportToXmlAsync(dlg.FileName, conns);
            BottomStatusText.Text = $"Exportado {conns.Count} conexões para XML do ConectaTI.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Exportar XML", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportAutoPuttyXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exportar Compatível com AutoPuTTY (XML)",
            Filter = "Arquivo AutoPuTTY (*.xml)|*.xml",
            FileName = "autoputty.xml"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var conns = await _repository.GetAllConnectionsAsync();
            await _importExportService.ExportToAutoPuttyXmlAsync(dlg.FileName, conns);
            BottomStatusText.Text = $"Exportado {conns.Count} conexões no formato compatível com AutoPuTTY.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Exportar AutoPuTTY XML", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateCsvMappingCombos(ParsedCsvDocument doc)
    {
        _suppressMappingChange = true;
        try
        {
            var options = new List<string> { "(Não mapear)" };
            for (var i = 0; i < doc.Headers.Count; i++)
            {
                options.Add($"{i + 1}: {doc.Headers[i]}");
            }

            SetupCombo(MapNameCombo, options, doc.SuggestedMapping.NameColumnIndex);
            SetupCombo(MapClientCombo, options, doc.SuggestedMapping.ClientColumnIndex);
            SetupCombo(MapGroupCombo, options, doc.SuggestedMapping.GroupColumnIndex);
            SetupCombo(MapEnvCombo, options, doc.SuggestedMapping.EnvironmentColumnIndex);
            SetupCombo(MapProtocolCombo, options, doc.SuggestedMapping.ProtocolColumnIndex);
            SetupCombo(MapHostCombo, options, doc.SuggestedMapping.HostColumnIndex);
            SetupCombo(MapPortCombo, options, doc.SuggestedMapping.PortColumnIndex);
            SetupCombo(MapUserCombo, options, doc.SuggestedMapping.UsernameColumnIndex);
        }
        finally
        {
            _suppressMappingChange = false;
        }
    }

    private static void SetupCombo(ComboBox combo, List<string> options, int suggestedColIndex)
    {
        combo.ItemsSource = options;
        combo.SelectedIndex = suggestedColIndex >= 0 && suggestedColIndex + 1 < options.Count
            ? suggestedColIndex + 1
            : 0;
    }

    private void CsvMappingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMappingChange || _currentCsvDoc == null)
            return;

        RebuildCsvPreviewFromCombos();
    }

    private void RebuildCsvPreviewFromCombos()
    {
        if (_currentCsvDoc == null)
            return;

        var map = new CsvColumnMapping
        {
            NameColumnIndex = MapNameCombo.SelectedIndex - 1,
            ClientColumnIndex = MapClientCombo.SelectedIndex - 1,
            GroupColumnIndex = MapGroupCombo.SelectedIndex - 1,
            EnvironmentColumnIndex = MapEnvCombo.SelectedIndex - 1,
            ProtocolColumnIndex = MapProtocolCombo.SelectedIndex - 1,
            HostColumnIndex = MapHostCombo.SelectedIndex - 1,
            PortColumnIndex = MapPortCombo.SelectedIndex - 1,
            UsernameColumnIndex = MapUserCombo.SelectedIndex - 1,
            DomainColumnIndex = _currentCsvDoc.SuggestedMapping.DomainColumnIndex,
            PrivateKeyPathColumnIndex = _currentCsvDoc.SuggestedMapping.PrivateKeyPathColumnIndex,
            TagsColumnIndex = _currentCsvDoc.SuggestedMapping.TagsColumnIndex,
            NotesColumnIndex = _currentCsvDoc.SuggestedMapping.NotesColumnIndex,
            FavoriteColumnIndex = _currentCsvDoc.SuggestedMapping.FavoriteColumnIndex
        };

        _currentPreview = _importExportService.BuildCsvPreview(_currentCsvDoc, map, _existingConnections);
        BindPreview(_currentPreview);
    }

    private void BindPreview(ImportPreviewResult preview)
    {
        PreviewSummaryText.Text = $"[{preview.SourceFormat}] {preview.DiagnosticMessage}";
        PreviewDataGrid.ItemsSource = preview.Items;
        CommitImportButton.IsEnabled = preview.ValidCount > 0;
        BottomStatusText.Text = preview.DiagnosticMessage;
    }

    private async void CommitImport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreview == null || _currentPreview.ValidCount == 0)
            return;

        var dupMode = DuplicateHandlingCombo.SelectedIndex switch
        {
            1 => DuplicateImportHandling.Update,
            2 => DuplicateImportHandling.CreateNew,
            _ => DuplicateImportHandling.Ignore
        };

        try
        {
            var report = await _importExportService.CommitImportAsync(_currentPreview, dupMode);
            DataModified = true;
            _existingConnections = await _repository.GetAllConnectionsAsync();
            BottomStatusText.Text = $"Importação concluída! Inseridos: {report.InsertedCount}, Atualizados: {report.UpdatedCount}, Ignorados: {report.IgnoredDuplicateCount}, Inválidos descartados: {report.InvalidSkippedCount}.";
            MessageBox.Show(
                this,
                $"Importação realizada com sucesso!\n\n" +
                $"• Novos registros inseridos: {report.InsertedCount}\n" +
                $"• Registros atualizados: {report.UpdatedCount}\n" +
                $"• Duplicados ignorados: {report.IgnoredDuplicateCount}\n" +
                $"• Linhas inválidas ignoradas: {report.InvalidSkippedCount}\n\n" +
                $"Backup automático salvo em:\n{report.PreImportBackupPath}",
                "Importação Concluída",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Erro ao importar: {ex.Message}", "Erro na Importação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exportar Cadastros para CSV (Sem Senhas)",
            Filter = "Arquivo CSV UTF-8 (*.csv)|*.csv",
            FileName = $"conectati-conexoes-{DateTime.Now:yyyyMMdd}.csv"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var all = await _repository.GetAllConnectionsAsync();
            await _importExportService.ExportToCsvAsync(dlg.FileName, all);
            BottomStatusText.Text = $"Exportado {all.Count} conexões para CSV (sem senhas).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Exportar CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exportar Cadastros para JSON (Sem Senhas)",
            Filter = "Arquivo JSON UTF-8 (*.json)|*.json",
            FileName = $"conectati-conexoes-{DateTime.Now:yyyyMMdd}.json"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var all = await _repository.GetAllConnectionsAsync();
            await _importExportService.ExportToJsonAsync(dlg.FileName, all);
            BottomStatusText.Text = $"Exportado {all.Count} conexões para JSON (sem senhas).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Exportar JSON", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportEncrypted_Click(object sender, RoutedEventArgs e)
    {
        var pass = TransportPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pass) || pass.Length < 6)
        {
            MessageBox.Show(this, "Informe uma senha de transporte com pelo menos 6 caracteres.", "Senha de Transporte", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Exportar Pacote Criptografado Portátil",
            Filter = "Pacote Criptografado ConectaTI (*.ctienc)|*.ctienc",
            FileName = $"conectati-pacote-seguro-{DateTime.Now:yyyyMMdd}.ctienc"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            await _importExportService.ExportEncryptedPackageAsync(dlg.FileName, pass);
            TransportPasswordBox.Clear();
            BottomStatusText.Text = "Pacote criptografado (.ctienc) exportado com sucesso (PBKDF2-SHA256 + AES-256-GCM).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Exportar Pacote Criptografado", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportEncrypted_Click(object sender, RoutedEventArgs e)
    {
        var pass = TransportPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pass))
        {
            MessageBox.Show(this, "Informe a senha de transporte do pacote .ctienc antes de importar.", "Senha de Transporte", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Importar Pacote Criptografado Portátil",
            Filter = "Pacote Criptografado ConectaTI (*.ctienc)|*.ctienc|Todos os arquivos (*.*)|*.*"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var (conns, creds, backupPath) = await _importExportService.ImportEncryptedPackageAsync(
                dlg.FileName,
                pass,
                DuplicateImportHandling.Update);

            TransportPasswordBox.Clear();
            DataModified = true;
            BottomStatusText.Text = $"Pacote importado: {conns} conexões e {creds} credenciais re-protegidas no cofre local.";
            MessageBox.Show(
                this,
                $"Pacote criptografado importado com sucesso!\n\n" +
                $"• Conexões importadas/atualizadas: {conns}\n" +
                $"• Credenciais importadas e protegidas via DPAPI local: {creds}\n\n" +
                $"Backup prévio salvo em:\n{backupPath}",
                "Importação Concluída",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Falha ao Importar Pacote Criptografado", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Salvar Backup Consistente do Banco SQLite",
            InitialDirectory = _paths.BackupsDirectory,
            Filter = "Banco SQLite ConectaTI (*.db)|*.db",
            FileName = $"conectati-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var path = await _repository.CreateBackupAsync(dlg.FileName);
            BottomStatusText.Text = $"Backup consistente criado em: {path}";
            MessageBox.Show(this, $"Backup consistente criado com sucesso em:\n{path}", "Backup Criado", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Falha ao criar backup: {ex.Message}", "Erro no Backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar Arquivo de Backup SQLite para Restaurar",
            InitialDirectory = _paths.BackupsDirectory,
            Filter = "Banco SQLite ConectaTI (*.db)|*.db|Todos os arquivos (*.*)|*.*"
        };

        if (dlg.ShowDialog() != true)
            return;

        var confirm = MessageBox.Show(
            this,
            $"Deseja restaurar o banco de dados a partir do arquivo:\n{dlg.FileName}?\n\n" +
            "Um backup de segurança do estado atual será criado automaticamente antes da restauração.",
            "Confirmar Restauração de Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _repository.RestoreBackupAsync(dlg.FileName);
            DataModified = true;
            BottomStatusText.Text = "Backup restaurado com sucesso.";
            MessageBox.Show(this, "Banco de dados restaurado com sucesso!", "Restauração Concluída", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"A restauração falhou e o banco original foi preservado:\n\n{ex.Message}", "Falha na Restauração", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
