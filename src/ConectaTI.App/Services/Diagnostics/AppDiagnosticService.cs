using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ConectaTI.App.Data;

namespace ConectaTI.App.Services.Diagnostics;

public record AppDiagnosticInfo(
    string AppVersion,
    string OperatingSystem,
    string Architecture,
    string BaseDirectory,
    string DataDirectory,
    bool IsDataDirectoryWritable,
    string DatabasePath,
    bool DatabaseExists,
    long DatabaseSizeBytes,
    int SchemaVersion,
    int ActiveConnectionsCount,
    int TrashConnectionsCount,
    string? LastSanitizedError)
{
    public string Runtime { get; init; } = string.Empty;
    public string SelectedLanguage { get; init; } = string.Empty;
    public string EffectiveLanguage { get; init; } = string.Empty;
    public long ProcessWorkingSetBytes { get; init; }
    public DateTime CollectedAtUtc { get; init; }
    public OperationFailure? Failure { get; init; }
}

public class AppDiagnosticService
{
    private readonly AppPaths _paths;
    private readonly SqliteRepository _repository;
    private readonly AppLogger _logger;

    public AppDiagnosticService(AppPaths paths, SqliteRepository repository, AppLogger logger)
    {
        _paths = paths;
        _repository = repository;
        _logger = logger;
    }

    public async Task<AppDiagnosticInfo> CollectDiagnosticInfoAsync()
    {
        var dbPath = _paths.DatabasePath;
        var dbExists = File.Exists(dbPath);
        long dbSize = 0;
        if (dbExists)
        {
            try
            {
                dbSize = new FileInfo(dbPath).Length;
            }
            catch
            {
            }
        }

        int schemaVersion = 0;
        try
        {
            schemaVersion = _repository.GetSchemaVersion();
        }
        catch
        {
        }

        int activeCount = 0;
        int trashCount = 0;
        try
        {
            activeCount = await _repository.GetActiveCountAsync();
            trashCount = await _repository.GetDeletedCountAsync();
        }
        catch
        {
        }

        return new AppDiagnosticInfo(
            AppVersion: "ConectaTI " + typeof(AppDiagnosticService).Assembly.GetName().Version,
            OperatingSystem: Environment.OSVersion.ToString(),
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            BaseDirectory: AppContext.BaseDirectory,
            DataDirectory: _paths.RootDirectory,
            IsDataDirectoryWritable: _paths.IsDataDirectoryWritable,
            DatabasePath: dbPath,
            DatabaseExists: dbExists,
            DatabaseSizeBytes: dbSize,
            SchemaVersion: schemaVersion,
            ActiveConnectionsCount: activeCount,
            TrashConnectionsCount: trashCount,
            LastSanitizedError: _logger.LastSanitizedError)
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            SelectedLanguage = LocalizationService.Current.SelectedLanguage.ToString(),
            EffectiveLanguage = LocalizationService.Current.EffectiveCulture.Name,
            ProcessWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
            CollectedAtUtc = DateTime.UtcNow,
            Failure = _logger.LastFailure
        };
    }

    public static string FormatReport(AppDiagnosticInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.Text("=== DIAGNÓSTICO DO CONECTATI ==="));
        sb.AppendLine(L.Format("Data da Coleta: {0:yyyy-MM-dd HH:mm:ss}", ((info.CollectedAtUtc == default ? DateTime.UtcNow : info.CollectedAtUtc).ToLocalTime())));
        sb.AppendLine(L.Format("Versão do Aplicativo: {0}", (info.AppVersion)));
        sb.AppendLine(L.Format("Sistema Operacional: {0} ({1})", (info.OperatingSystem), (info.Architecture)));
        sb.AppendLine(L.Format("Runtime: {0}", (info.Runtime)));
        sb.AppendLine(L.Format("Idioma selecionado: {0}", (info.SelectedLanguage)));
        sb.AppendLine(L.Format("Idioma efetivo: {0}", (info.EffectiveLanguage)));
        sb.AppendLine(L.Format("Memória do processo no momento da coleta (Working Set): {0} bytes", (info.ProcessWorkingSetBytes)));
        if (info.Failure is { } failure)
        {
            sb.Append(FormatFailure(info.Failure));
        }
        sb.AppendLine(L.Format("Pasta do Executável: {0}", (info.BaseDirectory)));
        sb.AppendLine(L.Format("Pasta de Dados Efetiva: {0}", (info.DataDirectory)));
        sb.AppendLine(L.Format("Permissão na Pasta de Dados: {0}", ((info.IsDataDirectoryWritable ? L.Text("Gravável (OK)") : L.Text("Somente Leitura (Restrita)")))));
        sb.AppendLine(L.Format("Caminho Efetivo do Banco: {0}", (info.DatabasePath)));
        sb.AppendLine(L.Format("Banco Existe: {0}", ((info.DatabaseExists ? L.Text("Sim") : L.Text("Não")))));
        if (info.DatabaseExists)
        {
            sb.AppendLine(L.Format("Tamanho do Banco: {0}", (FormatFileSize(info.DatabaseSizeBytes))));
        }
        sb.AppendLine(L.Format("Versão do Esquema SQLite: {0}", (info.SchemaVersion)));
        sb.AppendLine(L.Format("Conexões Ativas: {0}", (info.ActiveConnectionsCount)));
        sb.AppendLine(L.Format("Conexões na Lixeira: {0}", (info.TrashConnectionsCount)));
        sb.AppendLine(L.Format("Último Erro Registrado: {0}", ((string.IsNullOrWhiteSpace(info.LastSanitizedError) ? L.Text("Nenhum erro registrado") : info.LastSanitizedError))));
        sb.AppendLine("================================");
        sb.AppendLine(L.Text("(Este relatório é sanitizado e não contém senhas, chaves privadas ou dados confidenciais)."));
        return AppLogger.SanitizeMessage(sb.ToString());
    }

    public static string FormatFailure(OperationFailure failure)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.Format("Etapa: {0}", (failure.Stage)));
        sb.AppendLine(L.Format("Data do erro (UTC): {0:O}", (failure.AtUtc)));
        sb.AppendLine(L.Format("Exceção: {0}", (failure.ExceptionType)));
        sb.AppendLine(L.Format("Mensagem técnica: {0}", (failure.Message)));
        sb.AppendLine(L.Format("Stack trace: {0}", (failure.StackTrace)));
        if (failure.FileSize.HasValue) sb.AppendLine(L.Format("Tamanho do arquivo: {0} bytes", (failure.FileSize)));
        if (failure.IdentifiedCount.HasValue) sb.AppendLine(L.Format("Registros identificados: {0}", (failure.IdentifiedCount)));
        if (failure.Elapsed.HasValue) sb.AppendLine(L.Format("Tempo decorrido (ms): {0:F0}", (failure.Elapsed.Value.TotalMilliseconds)));
        if (failure.NativeApi != null) sb.AppendLine(L.Format("API nativa: {0}", failure.NativeApi));
        if (failure.NativeErrorCode.HasValue) sb.AppendLine(L.Format("Código de erro nativo Win32: {0}", failure.NativeErrorCode));
        if (failure.RecordIndex.HasValue) sb.AppendLine(L.Format("Índice do registro (base 1): {0}", failure.RecordIndex));
        if (failure.SourceRowNumber.HasValue) sb.AppendLine(L.Format("Linha de origem: {0}", failure.SourceRowNumber));
        if (failure.CredentialIndex.HasValue) sb.AppendLine(L.Format("Índice da credencial (base 1): {0}", failure.CredentialIndex));
        return AppLogger.SanitizeMessage(sb.ToString());
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
    }
}
