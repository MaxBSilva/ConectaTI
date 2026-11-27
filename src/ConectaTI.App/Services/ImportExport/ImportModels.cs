using ConectaTI.App.Models;
namespace ConectaTI.App.Services.ImportExport;

public class CsvColumnMapping
{
    public int NameColumnIndex { get; set; } = -1;
    public int ClientColumnIndex { get; set; } = -1;
    public int GroupColumnIndex { get; set; } = -1;
    public int EnvironmentColumnIndex { get; set; } = -1;
    public int ProtocolColumnIndex { get; set; } = -1;
    public int HostColumnIndex { get; set; } = -1;
    public int PortColumnIndex { get; set; } = -1;
    public int UsernameColumnIndex { get; set; } = -1;
    public int DomainColumnIndex { get; set; } = -1;
    public int PrivateKeyPathColumnIndex { get; set; } = -1;
    public int VpnNameColumnIndex { get; set; } = -1;
    public int VpnAppColumnIndex { get; set; } = -1;
    public int VpnServerColumnIndex { get; set; } = -1;
    public int VpnUsernameColumnIndex { get; set; } = -1;
    public int VpnNotesColumnIndex { get; set; } = -1;
    public int TagsColumnIndex { get; set; } = -1;
    public int NotesColumnIndex { get; set; } = -1;
    public int RdpConfigurationColumnIndex { get; set; } = -1;
    public int SshClientColumnIndex { get; set; } = -1;
    public int SshUseAgentColumnIndex { get; set; } = -1;
    public int VncParametersColumnIndex { get; set; } = -1;
    public int ActiveColumnIndex { get; set; } = -1;
    public int FavoriteColumnIndex { get; set; } = -1;
}

public class ParsedCsvDocument
{
    public char Delimiter { get; init; } = ';';
    public List<string> Headers { get; init; } = new();
    public List<string[]> Rows { get; init; } = new();
    public CsvColumnMapping SuggestedMapping { get; init; } = new();
}

public class ImportPreviewItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int RowNumber { get; init; }
    public ConnectionEntry Entry { get; init; } = new();
    public bool IsValid { get; init; }
    public bool IsDuplicate { get; init; }
    public string? ExistingConnectionId { get; init; }
    private string _statusText = string.Empty;
    private LocalizedMessage? _statusMessage;
    public string StatusText { get => L.Render(_statusMessage, _statusText); init { _statusText = value; _statusMessage = L.Capture(value); } }
    public bool IsSelected { get; set; } = true;
    public string? RecoveredPlainPassword { get; init; }
    internal string? SourceCredentialKey { get; init; }
    internal SavedCredential? SourceCredentialMetadata { get; init; }
    public string PasswordDisplay => !string.IsNullOrEmpty(RecoveredPlainPassword) ? "••••••••" : L.Text("(nenhuma)");
    private string _unrecoveredFields = string.Empty;
    public string UnrecoveredFields { get => L.Refresh(_unrecoveredFields); init => _unrecoveredFields = value; }
    public void RefreshDisplayProperties() { Entry.RefreshDisplayProperties(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(UnrecoveredFields)); OnPropertyChanged(nameof(PasswordDisplay)); }
}

public class ImportPreviewResult
{
    internal List<PortableCredentialRecord> StandaloneCredentials { get; init; } = new();
    public int StandaloneCredentialCount => StandaloneCredentials.Count;
    public string SourceFormat { get; init; } = string.Empty;
    public string DiagnosticMessage { get; init; } = string.Empty;
    public List<ImportPreviewItem> Items { get; init; } = new();
    public int ValidCount => Items.Count(i => i.IsValid);
    public int InvalidCount => Items.Count(i => !i.IsValid);
    public int DuplicateCount => Items.Count(i => i.IsValid && i.IsDuplicate);
}

public record ImportExecutionReport(
    int InsertedCount,
    int UpdatedCount,
    int IgnoredDuplicateCount,
    int InvalidSkippedCount,
    string PreImportBackupPath)
{
    public int CredentialsImportedCount { get; init; }
    public int FoundCount { get; init; }
    public int DuplicatedCount { get; init; }
    public int UnselectedCount { get; init; }
    public int IgnoredCount => IgnoredDuplicateCount + UnselectedCount;
    public string Outcome { get; init; } = "Completed";
    public bool CleanupPending { get; init; }
    public int StandaloneCredentialsImported { get; init; }
    public int StandaloneCredentialsIgnored { get; init; }
}

public class PortableEncryptedBundle
{
    public bool IncludesPasswords { get; set; } = true;
    public string FormatVersion { get; set; } = "ConectaTI-Portable-1.0";
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ConnectionEntry> Connections { get; set; } = new();
    public List<PortableCredentialRecord> Credentials { get; set; } = new();
}

public class PortableCredentialRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string PlainSecretForEncryptedBundleOnly { get; set; } = string.Empty;
}

