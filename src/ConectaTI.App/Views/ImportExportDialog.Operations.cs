using ConectaTI.App.Services.Diagnostics;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using Microsoft.Win32;

namespace ConectaTI.App.Views;

public partial class ImportExportDialog
{
    private CancellationTokenSource? _operationCts;
    private string _errorDetails = string.Empty;
    private string _currentStage = "Validating";
    private void SetOperationBusy(bool busy)
    {
        ImportTabs.IsEnabled = !busy;
        CancelOperationButton.IsEnabled = busy;
        ImportProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CommitImportButton.IsEnabled = !busy && _currentPreview != null && (_currentPreview.ValidCount > 0 || _currentPreview.StandaloneCredentialCount > 0);
    }

    private IProgress<ImportProgress> CreateProgress()
    {
        var operation = _operationCts;
        return new Progress<ImportProgress>(value =>
    {
        if (_operationCts != operation || operation == null) return;
        _currentStage = value.Stage;
        BottomStatusText.Text = value.Stage switch
        {
            "Validating" => L.Text("Validando arquivo"),
            "Decoding" => L.Format(value.Completed == 1 ? "Decodificando {0} acesso" : "Decodificando {0} acessos", value.Completed),
            "Credentials" => L.Format("Preparando credenciais: {0} de {1}", (value.Completed), (value.Total)),
            "Backup" => L.Text("Criando backup"),
            "Importing" => L.Format(value.Total == 1 ? "Importando {0} de {1} acesso" : "Importando {0} de {1} acessos", value.Completed, value.Total),
            "Committing" => L.Text("Confirmando gravação"),
            "Completed" => L.Text("Importação concluída"),
            _ => L.Text("Atualizando lista")
        };
        ImportProgressBar.IsIndeterminate = value.Total == 0;
        ImportProgressBar.Maximum = Math.Max(1, value.Total);
        ImportProgressBar.Value = value.Completed;
        if (value.Stage is "Committing" or "Completed") CancelOperationButton.IsEnabled = false;
    });
    }

    private async void SelectImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts != null) return;
        var fileDialog = new OpenFileDialog
        {
            Title = L.Text("Selecionar arquivo de importação"),
            Filter = L.Text("XML, CSV, JSON, AutoPuTTY|*.xml;*.csv;*.json;*.ini|Todos os arquivos|*.*")
        };
        if (fileDialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        _operationCts = cancellation;
        SetOperationBusy(true);
        _currentPreview = null;
        _currentCsvDoc = null;
        PreviewDataGrid.ItemsSource = null;
        CsvMappingPanel.Visibility = Visibility.Collapsed;
        SelectedFileText.Text = fileDialog.FileName;
        _errorDetails = string.Empty;
        CopyImportErrorsButton.IsEnabled = false;
        try
        {
            var progress = CreateProgress();
            progress.Report(new("Validating"));
            ImportLimits.CheckFile(fileDialog.FileName);
            _existingConnections = await _repository.GetAllConnectionsAsync();
            string? password = null;
            if (Path.GetExtension(fileDialog.FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                await Task.Run(() => AutoPuttyCodec.RequiresPassword(File.ReadAllText(fileDialog.FileName)), cancellation.Token))
            {
                password = PromptForXmlPassword();
                if (password == null) { BottomStatusText.Text = L.Text("Importação cancelada. Nenhuma alteração foi gravada."); return; }
            }
            _currentPreview = await _importExportService.ReadPreviewAsync(fileDialog.FileName, _existingConnections, password, cancellation.Token, progress);
            if (Path.GetExtension(fileDialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                _currentCsvDoc = await Task.Run(() => _importExportService.ParseCsvFile(fileDialog.FileName), cancellation.Token);
                PopulateCsvMappingCombos(_currentCsvDoc);
                CsvMappingPanel.Visibility = Visibility.Visible;
            }
            BindPreview(_currentPreview);
            _errorDetails = string.Join(Environment.NewLine, _currentPreview.Items
                .Where(i => !i.IsValid || !string.IsNullOrEmpty(i.UnrecoveredFields))
                .Select(i => L.Format("Linha {0}: {1}", i.RowNumber, i.IsValid ? L.Text("Credencial não recuperada") : L.Text("Registro inválido"))));
            CopyImportErrorsButton.IsEnabled = _errorDetails.Length != 0;
        }
        catch (OperationCanceledException) { BottomStatusText.Text = L.Text("Importação cancelada. Nenhuma alteração foi gravada."); }
        catch (Exception exception) { ShowOperationFailure(exception); }
        finally { _operationCts = null; SetOperationBusy(false); }
    }

    private async void CommitImport_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts != null || _currentPreview == null) return;
        PreviewDataGrid.CommitEdit();
        PreviewDataGrid.CommitEdit();
        using var cancellation = new CancellationTokenSource();
        _operationCts = cancellation;
        var mode = DuplicateHandlingCombo.SelectedIndex switch
        { 1 => DuplicateImportHandling.Update, 2 => DuplicateImportHandling.CreateNew, _ => DuplicateImportHandling.Ignore };
        SetOperationBusy(true);
        try
        {
            var report = await _importExportService.CommitImportAsync(_currentPreview, mode, token: cancellation.Token, progress: CreateProgress());
            DataModified = true; // The service returned only after the transaction committed.
            BottomStatusText.Text = L.Format("Concluída. Encontrados: {0}; importados: {1}; atualizados: {2}; ignorados: {3}; duplicados criados: {4}; erros: {5}.", (report.FoundCount), (report.InsertedCount - report.DuplicatedCount), (report.UpdatedCount), (report.IgnoredCount), (report.DuplicatedCount), (Math.Max(_currentPreview.Items.Count(i => !i.IsValid || !string.IsNullOrEmpty(i.UnrecoveredFields)), report.InvalidSkippedCount)));
            if (report.StandaloneCredentialsImported+report.StandaloneCredentialsIgnored>0)
                BottomStatusText.Text+=" "+L.Format("Credenciais avulsas: gravadas {0}; ignoradas {1}.",report.StandaloneCredentialsImported,report.StandaloneCredentialsIgnored);
            if (report.CleanupPending) BottomStatusText.Text += L.Text(" Limpeza do cofre pendente.");
            PreviewDataGrid.ItemsSource = null;
            _currentCsvDoc = null;
            _currentPreview = null; // Prevent an accidental second commit of the same preview.
        }
        catch (OperationCanceledException) { BottomStatusText.Text = L.Text("Importação cancelada. As gravações foram desfeitas."); }
        catch (Exception exception) { ShowOperationFailure(exception); }
        finally { _operationCts = null; SetOperationBusy(false); }
    }

    private void ShowOperationFailure(Exception exception)
    {
        BottomStatusText.Text = L.Text("A operação falhou. Nenhuma importação parcial foi confirmada. Copie o diagnóstico para obter detalhes.");
        _errorDetails = exception.Data[typeof(OperationFailure)] is OperationFailure failure
            ? AppDiagnosticService.FormatFailure(failure)
            : AppDiagnosticService.FormatFailure(new OperationFailure(_currentStage, DateTime.UtcNow, exception.GetType().FullName!, "[REDACTED: unstructured error message]", new System.Diagnostics.StackTrace(exception, true).ToString(), null, null, null));
        CopyImportErrorsButton.IsEnabled = true;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        CancelOperationButton.IsEnabled = false;
        BottomStatusText.Text = L.Text("Cancelando operação…");
    }

    private void CopyImportErrors_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_errorDetails);
    }
}
