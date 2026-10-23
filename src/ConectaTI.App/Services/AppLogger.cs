using System.Text.RegularExpressions;

namespace ConectaTI.App.Services;

/// <summary>
/// Serviço de log técnico local que sanitiza automaticamente eventuais padrões sensíveis
/// para garantir que senhas e segredos nunca sejam gravados em disco.
/// </summary>
public partial class AppLogger
{
    private readonly AppPaths _paths;
    private readonly Lock _writeLock = new();

    public string? LastSanitizedError { get; private set; }

    [GeneratedRegex(@"(?i)(password|passwd|senha|secret|token|-pw)\s*[:=]\s*([^\s,;""']+)", RegexOptions.Compiled)]
    private static partial Regex SensitivePatternRegex();

    public AppLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public void Info(string category, string message) => Write("INFO", category, message, null);
    public void Warn(string category, string message) => Write("WARN", category, message, null);
    public void Error(string category, string message, Exception? ex = null) => Write("ERROR", category, message, ex);

    public static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        return SensitivePatternRegex().Replace(message, "$1=[REDACTED]");
    }

    private void Write(string level, string category, string message, Exception? ex)
    {
        try
        {
            _paths.EnsureDirectories();
            var logFile = Path.Combine(_paths.LogsDirectory, $"conectati-{DateTime.Now:yyyyMMdd}.log");
            var safeMessage = SanitizeMessage(message);
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                LastSanitizedError = safeMessage + (ex != null ? $" ({ex.GetType().Name}: {SanitizeMessage(ex.Message)})" : string.Empty);
            }
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {safeMessage}";
            if (ex != null)
            {
                var safeEx = SanitizeMessage($"{ex.GetType().Name}: {ex.Message}");
                line += Environment.NewLine + $"   Exception: {safeEx}";
            }

            lock (_writeLock)
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Evita que falhas de I/O no log interrompam o aplicativo
        }
    }
}
