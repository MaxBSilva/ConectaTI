using System.ComponentModel;
using ConectaTI.App.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace ConectaTI.App.Services;

public sealed record OperationFailure(string Stage, DateTime AtUtc, string ExceptionType,
    string Message, string StackTrace, long? FileSize, int? IdentifiedCount, TimeSpan? Elapsed)
{
    public string? NativeApi { get; init; }
    public int? NativeErrorCode { get; init; }
    // All positions are 1-based; source row can differ from credential position when rows are skipped.
    public int? RecordIndex { get; init; }
    public int? SourceRowNumber { get; init; }
    public int? CredentialIndex { get; init; }
}

public partial class AppLogger
{
    public const long MaxLogBytes = 512 * 1024;
    private readonly AppPaths _paths;
    private readonly Lock _writeLock = new();
    private readonly Dictionary<string, int> _redactions = new(StringComparer.Ordinal);
    public string? LastSanitizedError { get; private set; }
    public OperationFailure? LastFailure { get; private set; }
    [GeneratedRegex("(?i)(password|passwd|pwd|senha|secret|token|authorization|api[_-]?key|-pw)\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex SensitivePatternRegex();
    [GeneratedRegex(@"(?is)<\?xml.*|<\s*(Data|List|Server|Password|Credentials?|Secret|ConnectionString)\b.*")]
    private static partial Regex PayloadRegex();
    [GeneratedRegex(@"(?i)(bearer\s+)[\w.\-]+|\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|sk-proj-[A-Za-z0-9_-]+)\b")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"(?i)([a-z][a-z0-9+.-]*://)[^\s/@]+:[^\s/@]+@")]
    private static partial Regex UriCredentialsRegex();
    [GeneratedRegex(@"(?is)-----BEGIN (?:[A-Z ]*PRIVATE KEY)-----.*?-----END (?:[A-Z ]*PRIVATE KEY)-----")]
    private static partial Regex PrivateKeyRegex();
    public AppLogger(AppPaths paths) => _paths = paths;
    public void Info(string category, string message) => Write("INFO", category, message, null);
    public void Warn(string category, string message) => Write("WARN", category, message, null);
    public void Error(string category, string message, Exception? ex = null) => Write("ERROR", category, message, ex);
    public static string SanitizeMessage(string? message)
    {
        var safe = PayloadRegex().Replace(message ?? string.Empty, "[XML REDACTED]");
        safe = SensitivePatternRegex().Replace(safe, "$1=[REDACTED]");
        safe = TokenRegex().Replace(safe, "[TOKEN REDACTED]");
        safe = UriCredentialsRegex().Replace(safe, "$1[REDACTED]@");
        safe = PrivateKeyRegex().Replace(safe, "[PRIVATE KEY REDACTED]");
        return safe.Length > 12_000 ? safe[..12_000] + "…" : safe;
    }
    // Free-form fields are untrusted. Redact the entire field instead of replacing substrings
    // inside class names, identifiers or the stack. Technical fields never pass through this scope.
    public string Sanitize(string? message)
    {
        var value = message ?? string.Empty;
        lock (_writeLock)
        {
            if (_redactions.Keys.Any(secret => secret.Length < 4) ||
                _redactions.Keys.Any(secret => value.Contains(secret, StringComparison.Ordinal)))
                return "[REDACTED: sensitive message field]";
        }
        return SanitizeMessage(value);
    }

    private string ExceptionMessage(Exception exception) => exception is NativeCredentialException native
        ? SanitizeMessage(new Win32Exception(native.NativeErrorCode).Message)
        : Sanitize(exception.Message);

    private static string TechnicalStack(Exception exception) =>
        SanitizeMessage(new System.Diagnostics.StackTrace(exception, true).ToString());

    public IDisposable Redact(IEnumerable<string?> values)
    {
        var secrets = values.Where(v => !string.IsNullOrEmpty(v)).Cast<string>().Distinct().ToArray();
        lock (_writeLock)
            foreach (var secret in secrets) _redactions[secret] = _redactions.GetValueOrDefault(secret) + 1;
        return new RedactionScope(() =>
        {
            lock (_writeLock)
                foreach (var secret in secrets)
                    if (--_redactions[secret] == 0) _redactions.Remove(secret);
        });
    }
    private sealed class RedactionScope(Action dispose) : IDisposable
    { private Action? _dispose = dispose; public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
    public void RecordFailure(string stage, Exception exception, long? fileSize = null, int? count = null, TimeSpan? elapsed = null,
        int? recordIndex = null, int? sourceRowNumber = null, int? credentialIndex = null)
    {
        // Outer service/UI catches may add context, but must retain the first failing call and position.
        if (exception.Data[typeof(OperationFailure)] is OperationFailure originalFailure)
        {
            LastFailure = originalFailure with
            {
                FileSize = fileSize ?? originalFailure.FileSize,
                IdentifiedCount = count ?? originalFailure.IdentifiedCount,
                Elapsed = elapsed ?? originalFailure.Elapsed
            };
            exception.Data[typeof(OperationFailure)] = LastFailure;
            return;
        }
        LastFailure = new(stage, DateTime.UtcNow, exception.GetType().FullName ?? exception.GetType().Name,
            ExceptionMessage(exception), TechnicalStack(exception), fileSize, count, elapsed)
        {
            NativeApi = (exception as NativeCredentialException)?.NativeApi,
            NativeErrorCode = (exception as Win32Exception)?.NativeErrorCode,
            RecordIndex = recordIndex, SourceRowNumber = sourceRowNumber, CredentialIndex = credentialIndex
        };
        exception.Data[typeof(OperationFailure)] = LastFailure;
        var failure = LastFailure;
        Write("ERROR", stage,
            $"Operation failed; NativeApi={failure.NativeApi ?? "-"}; NativeErrorCode={failure.NativeErrorCode?.ToString() ?? "-"}; " +
            $"RecordIndex={recordIndex?.ToString() ?? "-"}; SourceRowNumber={sourceRowNumber?.ToString() ?? "-"}; " +
            $"CredentialIndex={credentialIndex?.ToString() ?? "-"}; FileBytes={fileSize}; IdentifiedCount={count}; ElapsedMs={elapsed?.TotalMilliseconds:F0}",
            exception, preserveFailure: true);
    }
    internal void EnrichImportFailure(DateTime startedAtUtc, long? fileSize, int? count, TimeSpan elapsed)
    {
        if (LastFailure is { } failure && failure.AtUtc >= startedAtUtc && failure.Stage.StartsWith("Import.", StringComparison.Ordinal))
            LastFailure = failure with { FileSize = fileSize, IdentifiedCount = count, Elapsed = elapsed };
    }
    private void Write(string level, string category, string message, Exception? exception, bool preserveFailure = false)
    {
        try
        {
            _paths.EnsureDirectories();
            var safe = preserveFailure ? SanitizeMessage(message) : Sanitize(message);
            var detail = exception == null ? string.Empty : $"{exception.GetType().FullName}: {ExceptionMessage(exception)}";
            if (level == "ERROR")
            {
                LastSanitizedError = safe + (detail.Length == 0 ? string.Empty : " (" + detail + ")");
                if (exception != null && !preserveFailure)
                    LastFailure = new(category, DateTime.UtcNow, exception.GetType().FullName!, ExceptionMessage(exception), TechnicalStack(exception), null, null, null);
            }
            if (preserveFailure && exception != null) detail += Environment.NewLine + TechnicalStack(exception);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {safe} {detail}" + Environment.NewLine;
            var path = Path.Combine(_paths.LogsDirectory, $"conectati-{DateTime.Now:yyyyMMdd}.log");
            lock (_writeLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaxLogBytes)
                {
                    for (var i = 2; i >= 1; i--)
                        if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
                foreach (var file in new DirectoryInfo(_paths.LogsDirectory).EnumerateFiles("conectati-*.log*")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(12)) file.Delete();
            }
        }
        catch { }
    }
}
