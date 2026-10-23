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
    string? LastSanitizedError);

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
            AppVersion: "ConectaTI v1.2.1 (Compacto)",
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
            LastSanitizedError: _logger.LastSanitizedError);
    }

    public static string FormatReport(AppDiagnosticInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== DIAGNÓSTICO DO CONECTATI ===");
        sb.AppendLine($"Data da Coleta: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Versão do Aplicativo: {info.AppVersion}");
        sb.AppendLine($"Sistema Operacional: {info.OperatingSystem} ({info.Architecture})");
        sb.AppendLine($"Pasta do Executável: {info.BaseDirectory}");
        sb.AppendLine($"Pasta de Dados Efetiva: {info.DataDirectory}");
        sb.AppendLine($"Permissão na Pasta de Dados: {(info.IsDataDirectoryWritable ? "Gravável (OK)" : "Somente Leitura (Restrita)")}");
        sb.AppendLine($"Caminho Efetivo do Banco: {info.DatabasePath}");
        sb.AppendLine($"Banco Existe: {(info.DatabaseExists ? "Sim" : "Não")}");
        if (info.DatabaseExists)
        {
            sb.AppendLine($"Tamanho do Banco: {FormatFileSize(info.DatabaseSizeBytes)}");
        }
        sb.AppendLine($"Versão do Esquema SQLite: {info.SchemaVersion}");
        sb.AppendLine($"Conexões Ativas: {info.ActiveConnectionsCount}");
        sb.AppendLine($"Conexões na Lixeira: {info.TrashConnectionsCount}");
        sb.AppendLine($"Último Erro Registrado: {(string.IsNullOrWhiteSpace(info.LastSanitizedError) ? "Nenhum erro registrado" : info.LastSanitizedError)}");
        sb.AppendLine("================================");
        sb.AppendLine("(Este relatório é sanitizado e não contém senhas, chaves privadas ou dados confidenciais).");
        return sb.ToString();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
    }
}
