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
    private readonly SettingsService? _settings;
    private readonly SqliteRepository _repository;
    private readonly ImportExportService _importExportService;

    private ParsedCsvDocument? _currentCsvDoc;
    private ImportPreviewResult? _currentPreview;
    private List<ConnectionEntry> _existingConnections = new();
    private bool _suppressMappingChange;
    private readonly ExportSelectionModel _exportSelection = new();

    public bool DataModified { get; private set; }

    public ImportExportDialog(
        AppPaths paths,
        SqliteRepository repository,
        ImportExportService importExportService, SettingsService? settings = null)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _settings = settings;
        if (settings != null)
        {
            ViewPreferencesService.Normalize(settings.Current);
            foreach (var column in PreviewDataGrid.Columns)
                if (settings.Current.ImportColumnWidths.TryGetValue(ColumnKey(column), out var width)) column.Width = new DataGridLength(width);
            Closed += (_, _) =>
            {
                foreach (var column in PreviewDataGrid.Columns)
                    if (column.ActualWidth is >= 40 and <= 700) settings.Current.ImportColumnWidths[ColumnKey(column)] = column.ActualWidth;
                settings.Save();
            };
        }
        ExportSelectionPanel.DataContext=_exportSelection;
        _paths = paths;
        _repository = repository;
        _importExportService = importExportService;
        ImportTabs.SelectionChanged+=async (_,e)=>
        {
            if(e.Source==ImportTabs && ImportTabs.SelectedIndex==1 && _operationCts==null)
                _exportSelection.Reload(await _repository.GetAllConnectionsAsync());
        };
        Closed+=(_,_)=>{TransportPasswordBox.Clear();TransportConfirmationBox.Clear();};
        Closing += (_, e) => { if (_operationCts != null) { _operationCts.Cancel(); e.Cancel = true; } };

        BackupsFolderInfoText.Text = L.Format("Pasta padrão de backups automáticos: {0}\n", (_paths.BackupsDirectory)) +
                                     L.Text("Backup local do SQLite mantém a proteção do usuário de origem. Restaurar não transfere senhas para outro perfil. Para isso, exporte um pacote de transferência com senhas. A restauração verifica a integridade e preserva uma cópia de segurança.");

        Loaded += async (_, _) =>
        {
            _existingConnections = await _repository.GetAllConnectionsAsync();
            _exportSelection.Reload(_existingConnections);
        };
    }

    private static string ColumnKey(DataGridColumn column) =>
        column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding } ? binding.Path.Path : column.SortMemberPath;

    private string? PromptForXmlPassword()
    {
        var win = new Window
        {
            Title = L.Text("Senha do Arquivo AutoPuTTY"),
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
            Text = L.Text("O arquivo está protegido por senha mestra. Digite a senha para decodificar as conexões:"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 12
        });

        var pwdBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
        stack.Children.Add(pwdBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "OK", Width = 75, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = L.Text("Cancelar"), Width = 75, Padding = new Thickness(0, 4, 0, 4), IsCancel = true };

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
            Title = L.Text("Exportar Cadastros para XML (ConectaTI)"),
            Filter = L.Text("Arquivo XML (*.xml)|*.xml"),
            FileName = $"ConectaTI-Export-{DateTime.Now:yyyyMMdd-HHmm}.xml"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var conns = (await _repository.GetAllConnectionsAsync()).Where(c=>_exportSelection.SelectedIds.Contains(c.Id)).ToList();
            await _importExportService.ExportToXmlAsync(dlg.FileName, conns);
            BottomStatusText.Text = L.Format("Exportado {0} conexões para XML do ConectaTI.", (conns.Count));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Text("Erro ao Exportar XML"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportAutoPuttyXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = L.Text("Exportar Compatível com AutoPuTTY (XML)"),
            Filter = L.Text("Arquivo AutoPuTTY (*.xml)|*.xml"),
            FileName = "autoputty.xml"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var conns = (await _repository.GetAllConnectionsAsync()).Where(c=>_exportSelection.SelectedIds.Contains(c.Id)).ToList();
            await _importExportService.ExportToAutoPuttyXmlAsync(dlg.FileName, conns);
            BottomStatusText.Text = L.Format("Exportado {0} conexões no formato compatível com AutoPuTTY.", (conns.Count));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Text("Erro ao Exportar AutoPuTTY XML"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateCsvMappingCombos(ParsedCsvDocument doc)
    {
        _suppressMappingChange = true;
        try
        {
            var options = new List<string> { L.Text("(Não mapear)") };
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

    private async void CsvMappingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMappingChange || _currentCsvDoc == null)
            return;

        await RebuildCsvPreviewFromCombosAsync();
    }

    private async Task RebuildCsvPreviewFromCombosAsync()
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

        if (_operationCts != null) return;
        var document = _currentCsvDoc;
        using var cancellation = new CancellationTokenSource();
        _operationCts = cancellation;
        SetOperationBusy(true);
        try
        {
            var progress = CreateProgress();
            _currentPreview = await Task.Run(() =>
            {
                using var scope = AutoPuttyCodec.BeginReadScope(cancellation.Token, progress);
                return _importExportService.BuildCsvPreview(document, map, _existingConnections);
            }, cancellation.Token);
            BindPreview(_currentPreview);
        }
        catch (OperationCanceledException) { BottomStatusText.Text = L.Text("Importação cancelada. Nenhuma alteração foi gravada."); }
        catch (Exception ex) { ShowOperationFailure(ex); }
        finally { _operationCts = null; SetOperationBusy(false); }
    }

    private void BindPreview(ImportPreviewResult preview)
    {
        PreviewSummaryText.Text = $"[{preview.SourceFormat}] {preview.DiagnosticMessage}";
        PreviewDataGrid.ItemsSource = preview.Items;
        CommitImportButton.IsEnabled = preview.ValidCount > 0 || preview.StandaloneCredentialCount > 0;
        BottomStatusText.Text = preview.DiagnosticMessage;
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = L.Text("Exportar Cadastros para CSV (Sem Senhas)"),
            Filter = L.Text("Arquivo CSV UTF-8 (*.csv)|*.csv"),
            FileName = $"conectati-conexoes-{DateTime.Now:yyyyMMdd}.csv"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var all = (await _repository.GetAllConnectionsAsync()).Where(c=>_exportSelection.SelectedIds.Contains(c.Id)).ToList();
            await _importExportService.ExportToCsvAsync(dlg.FileName, all);
            BottomStatusText.Text = L.Format("Exportado {0} conexões para CSV (sem senhas).", (all.Count));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Text("Erro ao Exportar CSV"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = L.Text("Exportar Cadastros para JSON (Sem Senhas)"),
            Filter = L.Text("Arquivo JSON UTF-8 (*.json)|*.json"),
            FileName = $"conectati-conexoes-{DateTime.Now:yyyyMMdd}.json"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var all = (await _repository.GetAllConnectionsAsync()).Where(c=>_exportSelection.SelectedIds.Contains(c.Id)).ToList();
            await _importExportService.ExportToJsonAsync(dlg.FileName, all);
            BottomStatusText.Text = L.Format("Exportado {0} conexões para JSON (sem senhas).", (all.Count));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Text("Erro ao Exportar JSON"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectFilteredExport_Click(object sender,RoutedEventArgs e)=>_exportSelection.SelectFiltered();
    private void ClearExportSelection_Click(object sender,RoutedEventArgs e)=>_exportSelection.Clear();
    private void IncludePasswords_Changed(object sender,RoutedEventArgs e)
    {
        if(ExportPasswordPanel!=null)ExportPasswordPanel.Visibility=IncludePasswordsCheck.IsChecked==true ? Visibility.Visible : Visibility.Collapsed;
        if(IncludePasswordsCheck.IsChecked!=true){TransportPasswordBox?.Clear();TransportConfirmationBox?.Clear();}
    }
    private async void ExportEncrypted_Click(object sender,RoutedEventArgs e)
    {
        if(_operationCts!=null)return;
        ExportSelectionGrid.CommitEdit();ExportSelectionGrid.CommitEdit();
        var ids=_exportSelection.SelectedIds;
        var include=IncludePasswordsCheck.IsChecked==true;
        var pass=TransportPasswordBox.Password;var confirmation=TransportConfirmationBox.Password;
        if(ids.Length==0){BottomStatusText.Text=L.Text("Selecione pelo menos um acesso.");return;}
        if(include && (pass.Length<8 || pass!=confirmation))
        {BottomStatusText.Text=L.Text("Informe uma senha de proteção com pelo menos 8 caracteres e confirme a mesma senha.");return;}
        var dlg=new SaveFileDialog {Title=L.Text("Exportar seleção"),Filter=include
            ? L.Text("Pacote com senhas (*.ctienc)|*.ctienc") : L.Text("Pacote sem senhas (*.ctishare)|*.ctishare"),
            FileName=$"conectati-transfer-{DateTime.Now:yyyyMMdd}"+(include?".ctienc":".ctishare")};
        if(dlg.ShowDialog(this)!=true)return;
        using var cancellation=new CancellationTokenSource();_operationCts=cancellation;SetOperationBusy(true);
        try
        {
            await _importExportService.ExportTransferPackageAsync(dlg.FileName,ids,include,pass,confirmation,cancellation.Token);
            TransportPasswordBox.Clear();TransportConfirmationBox.Clear();
            BottomStatusText.Text=L.Format("Pacote de transferência exportado: {0} acesso(s).",ids.Length);
        }
        catch(OperationCanceledException){BottomStatusText.Text=L.Text("Exportação cancelada. Nenhum pacote foi gerado.");}
        catch(Exception ex){BottomStatusText.Text=ex.Message;}
        finally{_operationCts=null;SetOperationBusy(false);}
    }
    private async void ImportEncrypted_Click(object sender,RoutedEventArgs e)
    {
        if(_operationCts!=null)return;
        var dialog=new OpenFileDialog {Title=L.Text("Importar pacote de transferência"),
            Filter=L.Text("Pacotes ConectaTI|*.ctienc;*.ctishare|Todos os arquivos|*.*")};
        if(dialog.ShowDialog(this)!=true)return;
        var prefix=new byte[8];
        try { ImportLimits.CheckFile(dialog.FileName);using var input=File.OpenRead(dialog.FileName);await input.ReadExactlyAsync(prefix); }
        catch(Exception ex){ShowOperationFailure(ex);return;}
        var metadataOnly=prefix.AsSpan().SequenceEqual("CTIMETA2"u8);
        var pass=metadataOnly ? "" : PromptForTransferPassword();
        if(pass==null)return;
        using var cancellation=new CancellationTokenSource();_operationCts=cancellation;SetOperationBusy(true);
        try
        {
            _currentCsvDoc=null;
            _currentPreview=await _importExportService.ReadEncryptedPreviewAsync(dialog.FileName,pass,cancellation.Token,CreateProgress());
            SelectedFileText.Text=dialog.FileName;CsvMappingPanel.Visibility=Visibility.Collapsed;
            BindPreview(_currentPreview);ImportTabs.SelectedIndex=0;
        }
        catch(OperationCanceledException){BottomStatusText.Text=L.Text("Importação cancelada. Nenhuma alteração foi gravada.");}
        catch(Exception ex){ShowOperationFailure(ex);}
        finally{_operationCts=null;SetOperationBusy(false);}
    }
    private string? PromptForTransferPassword()
    {
        var window=new Window {Title=L.Text("Senha de proteção do arquivo"),Owner=this,Width=440,Height=180,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(18)};var box=new PasswordBox{Margin=new Thickness(0,8,0,12)};
        panel.Children.Add(new TextBlock{Text=L.Text("Informe a senha do pacote criptografado."),TextWrapping=TextWrapping.Wrap});panel.Children.Add(box);
        var ok=new Button{Content=L.Text("Continuar"),IsDefault=true,Padding=new Thickness(10,5,10,5)};
        ok.Click+=(_,_)=>window.DialogResult=true;panel.Children.Add(ok);window.Content=panel;
        var result=window.ShowDialog()==true ? box.Password : null;box.Clear();return result;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = L.Text("Salvar Backup Consistente do Banco SQLite"),
            InitialDirectory = _paths.BackupsDirectory,
            Filter = "Banco SQLite ConectaTI (*.db)|*.db",
            FileName = $"conectati-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var path = await _repository.CreateBackupAsync(dlg.FileName);
            BottomStatusText.Text = L.Format("Backup consistente criado em: {0}", (path));
            MessageBox.Show(this, L.Format("Backup consistente criado com sucesso em:\n{0}", (path)), L.Text("Backup Criado"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.Format("Falha ao criar backup: {0}", (ex.Message)), L.Text("Erro no Backup"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.Text("Selecionar Arquivo de Backup SQLite para Restaurar"),
            InitialDirectory = _paths.BackupsDirectory,
            Filter = L.Text("Banco SQLite ConectaTI (*.db)|*.db|Todos os arquivos (*.*)|*.*")
        };

        if (dlg.ShowDialog() != true)
            return;

        var confirm = MessageBox.Show(
            this,
            L.Format("Deseja restaurar o banco de dados a partir do arquivo:\n{0}?\n\n", (dlg.FileName)) +
            L.Text("Um backup de segurança do estado atual será criado automaticamente antes da restauração."),
            L.Text("Confirmar Restauração de Backup"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _importExportService.LockVaultSession();
            await _repository.RestoreBackupAsync(dlg.FileName);
            DataModified = true;
            BottomStatusText.Text = L.Text("Backup restaurado com sucesso.");
            MessageBox.Show(this, L.Text("Banco de dados restaurado com sucesso!"), L.Text("Restauração Concluída"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.Format("A restauração falhou e o banco original foi preservado:\n\n{0}", (ex.Message)), L.Text("Falha na Restauração"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
