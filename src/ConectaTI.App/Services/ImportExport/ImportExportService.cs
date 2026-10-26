using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

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
    public int VpnNotesColumnIndex { get; set; } = -1;
    public int TagsColumnIndex { get; set; } = -1;
    public int NotesColumnIndex { get; set; } = -1;
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

/// <summary>
/// Serviço completo de Importação (CSV, JSON, AutoPuTTY não criptografado),
/// Exportação (CSV/JSON sem senhas por padrão) e Pacote Criptografado Portátil (AES-256-GCM + PBKDF2).
/// Cria backup consistente do SQLite antes de qualquer importação.
/// </summary>
public partial class ImportExportService
{
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;
    private readonly AppLogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ImportExportService(
        SqliteRepository repository,
        CredentialVaultService vaultService,
        AppLogger logger)
    {
        _repository = repository;
        _vaultService = vaultService;
        _logger = logger;
    }

    // =========================================================================
    // Exportação Padrão (CSV e JSON — SEM senhas por padrão)
    // =========================================================================

    public async Task ExportToCsvAsync(string filePath, IEnumerable<ConnectionEntry> connections)
    {
        await Task.Run(() =>
        {
            var sb = new StringBuilder();
            var headers = new[]
            {
                "Nome", "Cliente", "Grupo", "Ambiente", "Protocolo",
                "Host", "Porta", "Usuario", "Dominio", "ChavePrivada",
                "VpnNome", "VpnAplicativo", "VpnServidor", "VpnObservacoes",
                "Tags", "Observacoes", "Favorito"
            };
            sb.AppendLine(string.Join(";", headers.Select(EscapeCsvField)));

            foreach (var c in connections)
            {
                var row = new[]
                {
                    c.Name,
                    c.ClientName,
                    c.GroupName,
                    c.Environment.ToInvariantDisplayName(),
                    c.Protocol.ToString(),
                    c.Host,
                    c.Port.ToString(CultureInfo.InvariantCulture),
                    c.Username,
                    c.Domain,
                    c.PrivateKeyPath,
                    c.VpnName,
                    c.VpnApp,
                    c.VpnServer,
                    c.VpnNotes,
                    c.Tags,
                    c.Notes,
                    c.IsFavorite ? "Sim" : "Não"
                };
                sb.AppendLine(string.Join(";", row.Select(EscapeCsvField)));
            }

            // Grava em UTF-8 com BOM para preservação perfeita de acentos no Windows/Excel
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            _logger.Info("Export", $"Exportação CSV concluída (sem senhas) para '{filePath}'.");
        });
    }

    public async Task ExportToJsonAsync(string filePath, IEnumerable<ConnectionEntry> connections)
    {
        await Task.Run(() =>
        {
            // Clona registros limpando CredentialId caso o usuário queira apenas os cadastros puros sem segredos
            var sanitized = connections.Select(c =>
            {
                var clone = c.Clone();
                return clone;
            }).ToList();

            var json = JsonSerializer.Serialize(sanitized, JsonOptions);
            File.WriteAllText(filePath, json, new UTF8Encoding(true));
            _logger.Info("Export", $"Exportação JSON concluída (sem senhas) para '{filePath}'.");
        });
    }

    public async Task ExportToXmlAsync(string filePath, IEnumerable<ConnectionEntry> connections)
    {
        await Task.Run(() =>
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("ConectaTI",
                    new XAttribute("Version", "1.0"),
                    new XAttribute("ExportedAtUtc", DateTime.UtcNow.ToString("o")),
                    new XElement("Connections",
                        connections.Select(c => new XElement("Connection",
                            new XElement("Id", c.Id),
                            new XElement("Name", c.Name),
                            new XElement("Client", c.ClientName),
                            new XElement("Group", c.GroupName),
                            new XElement("Environment", c.Environment.ToString()),
                            new XElement("Protocol", c.Protocol.ToString()),
                            new XElement("SshClient", c.SshClient.ToString()),
                            new XElement("Host", c.Host),
                            new XElement("Port", c.Port),
                            new XElement("Username", c.Username),
                            new XElement("Domain", c.Domain),
                            new XElement("PrivateKeyPath", c.PrivateKeyPath),
                            new XElement("VpnName", c.VpnName),
                            new XElement("VpnApp", c.VpnApp),
                            new XElement("VpnServer", c.VpnServer),
                            new XElement("VpnNotes", c.VpnNotes),
                            new XElement("Tags", c.Tags),
                            new XElement("Notes", c.Notes),
                            new XElement("IsFavorite", c.IsFavorite)
                        ))
                    )
                )
            );

            doc.Save(filePath);
            _logger.Info("Export", $"Exportação XML próprio ConectaTI concluída para '{filePath}'.");
        });
    }

    public async Task ExportToAutoPuttyXmlAsync(string filePath, IEnumerable<ConnectionEntry> connections)
    {
        await Task.Run(() =>
        {
            var xml = AutoPuttyCodec.GenerateCompatibleAutoPuttyXml(connections);
            File.WriteAllText(filePath, xml, new UTF8Encoding(false));
            _logger.Info("Export", $"Exportação XML compatível com AutoPuTTY concluída para '{filePath}'.");
        });
    }

    // =========================================================================
    // Exportação e Importação Criptografada Portátil (Entre Computadores)
    // =========================================================================

    public async Task ExportEncryptedPackageAsync(string filePath,string transportPassword) =>
        await ExportTransferPackageAsync(filePath,(await _repository.GetAllConnectionsAsync()).Select(c=>c.Id).ToArray(),
            true,transportPassword,transportPassword);

    public async Task ExportTransferPackageAsync(string filePath,IReadOnlyCollection<string> selectedIds,
        bool includePasswords,string password="",string confirmation="",CancellationToken token=default)
    {
        var ids=selectedIds.ToHashSet(StringComparer.Ordinal);
        if(ids.Count==0)throw new ArgumentException(L.Text("Selecione pelo menos um acesso."));
        if(includePasswords && (password.Length<8 || password!=confirmation))
            throw new ArgumentException(L.Text("Informe uma senha de proteção com pelo menos 8 caracteres e confirme a mesma senha."));
        using var redaction=_logger.Redact(new[]{password,confirmation});
        await _repository.CredentialOperationGate.WaitAsync(token);
        byte[]? json=null;
        try
        {
            var all=await _repository.GetAllConnectionsAsync();
            var selected=all.Where(c=>!c.IsDeleted && ids.Contains(c.Id)).Select(c=>c.Clone()).ToList();
            if(selected.Count!=ids.Count)throw new InvalidOperationException(L.Text("A seleção mudou ou contém acessos da lixeira. Revise antes de exportar."));
            var bundle=new PortableEncryptedBundle {FormatVersion="ConectaTI-Portable-2.0",
                IncludesPasswords=includePasswords,Connections=selected};
            if(includePasswords)
            {
                var linked=selected.Where(c=>!string.IsNullOrEmpty(c.CredentialId)).Select(c=>c.CredentialId!).ToHashSet();
                var credentials=(await _repository.GetAllCredentialsAsync()).Where(c=>linked.Contains(c.Id)).ToList();
                if(credentials.Count!=linked.Count)throw new InvalidDataException(L.Text("A seleção contém vínculos de credenciais inválidos."));
                var blocked=0;
                foreach(var credential in credentials)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var plain=await Task.Run(()=>_vaultService.ReadSecretForTransfer(credential),token);
                        if(string.IsNullOrEmpty(plain))throw new System.Security.Cryptography.CryptographicException();
                        bundle.Credentials.Add(new PortableCredentialRecord {Id=credential.Id,Name=credential.Name,Username=credential.Username,
                            Domain=credential.Domain,Notes=credential.Notes,PlainSecretForEncryptedBundleOnly=plain});
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException)
                    { blocked++; _logger.RecordFailure("Export.Credentials.Read",ex,recordIndex:blocked); }
                }
                if(blocked>0)throw new InvalidOperationException(L.Format("Exportação impedida: {0} credencial(is) selecionada(s) indisponível(is). Nenhum arquivo foi gerado. Importe um backup com senhas, migre o formato anterior ou informe uma nova senha.",blocked));
            }
            else foreach(var entry in selected)entry.CredentialId=null;
            json=JsonSerializer.SerializeToUtf8Bytes(bundle,JsonOptions);
            ImportLimits.CheckCount(selected.Count);
            if(json.Length>ImportLimits.MaxFileBytes-8192)throw new InvalidDataException(L.Text("O arquivo excede o limite de 32 MB."));
            var bytes=includePasswords
                ? await Task.Run(()=>CredentialVaultService.EncryptPortablePayload(json,password),token)
                : "CTIMETA2"u8.ToArray().Concat(json).ToArray();
            token.ThrowIfCancellationRequested();
            // Only fully prepared output is written. The temporary file, if used, contains ciphertext or metadata without passwords.
            var temp=filePath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp,bytes,token);
                token.ThrowIfCancellationRequested();
                File.Move(temp,filePath,true);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        finally
        {
            if(json!=null)System.Security.Cryptography.CryptographicOperations.ZeroMemory(json);
            _repository.CredentialOperationGate.Release();
        }
    }

    public async Task<(int ConnectionsImported, int CredentialsImported, string BackupPath)> ImportEncryptedPackageAsync(
        string filePath, string transportPassword, DuplicateImportHandling duplicateHandling,
        CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        var preview = await ReadEncryptedPreviewAsync(filePath, transportPassword, token, progress);
        var report = await CommitImportAsync(preview, duplicateHandling, token: token, progress: progress);
        return (report.InsertedCount + report.UpdatedCount, report.CredentialsImportedCount, report.PreImportBackupPath);
    }
    public async Task<ImportPreviewResult> ReadEncryptedPreviewAsync(string filePath, string transportPassword,
        CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        if (!await _importGate.WaitAsync(0, token)) throw new InvalidOperationException(L.Text("Já existe uma importação em andamento."));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var redaction = _logger.Redact([transportPassword]);
        try
        {
        ImportLimits.CheckFile(filePath);
        progress?.Report(new("Validating"));
        var bundle = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var encrypted = File.ReadAllBytes(filePath);
            var metadataOnly=encrypted.AsSpan().StartsWith("CTIMETA2"u8);
            var bytes = metadataOnly ? encrypted[8..] : CredentialVaultService.DecryptPortablePayload(encrypted, transportPassword);
            try
            {
                var result=JsonSerializer.Deserialize<PortableEncryptedBundle>(bytes, JsonOptions)
                    ?? throw new InvalidDataException(L.Text("Não foi possível ler o conteúdo do pacote criptografado."));
                ValidateTransferBundle(result,metadataOnly);
                return result;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }, token);
        ImportLimits.CheckCount(bundle.Connections.Count);
        ImportLimits.CheckCount(bundle.Credentials.Count);
        foreach(var credential in bundle.Credentials)
            if(new[]{credential.Id,credential.Name,credential.Username,credential.Domain,credential.Notes,credential.PlainSecretForEncryptedBundleOnly}
                .Any(value=>value?.Length>ImportLimits.MaxFieldCharacters))
                throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
        if (bundle.Credentials.GroupBy(c=>c.Id).Any(g=>g.Count()!=1) ||
            bundle.Connections.Any(c=>!string.IsNullOrEmpty(c.CredentialId) && !bundle.Credentials.Any(v=>v.Id==c.CredentialId)))
            throw new InvalidDataException(L.Text("Pacote com vínculos de credenciais inválidos."));
        var credentialMetadata=bundle.Credentials.ToDictionary(c=>c.Id);
        var secrets = bundle.Credentials.ToDictionary(c=>c.Id,c=>c.PlainSecretForEncryptedBundleOnly);
        var existing = await _repository.GetAllConnectionsAsync();
        var preview = await Task.Run(() => BuildPreviewFromCandidates(bundle.Connections, existing, L.Text("Pacote Criptografado (.ctienc)")), token);
        var items = preview.Items.Select(i => new ImportPreviewItem
        {
            RowNumber = i.RowNumber, Entry = i.Entry, IsValid = i.IsValid, IsDuplicate = i.IsDuplicate,
            ExistingConnectionId = i.ExistingConnectionId, StatusText = i.StatusText, IsSelected = i.IsValid,
            SourceCredentialKey=i.Entry.CredentialId,
            SourceCredentialMetadata=i.Entry.CredentialId != null && credentialMetadata.TryGetValue(i.Entry.CredentialId,out var source)
                ? new SavedCredential { Name=source.Name,Username=source.Username,Domain=source.Domain,Notes=source.Notes } : null,
            RecoveredPlainPassword = i.Entry.CredentialId != null ? secrets.GetValueOrDefault(i.Entry.CredentialId) : null
        }).ToList();
        var referencedSourceIds=items.Select(i=>i.SourceCredentialKey).ToHashSet();
        foreach (var item in items) item.Entry.CredentialId = null;
        var standalone=bundle.Credentials.Where(c=>!referencedSourceIds.Contains(c.Id)).ToList();
        preview = new ImportPreviewResult { Items = items, SourceFormat = preview.SourceFormat, StandaloneCredentials=standalone,
            DiagnosticMessage = preview.DiagnosticMessage + (standalone.Count>0
                ? " "+L.Format("Credenciais avulsas incluídas no pacote: {0}.",standalone.Count) : "") };

        token.ThrowIfCancellationRequested();
        LastImportFileSize = new FileInfo(filePath).Length;
        LastIdentifiedCount = preview.Items.Count;
        return preview;
        }
        catch (Exception ex) { _logger.RecordFailure("Import.Package", ex, LastImportFileSize, LastIdentifiedCount, clock.Elapsed); throw; }
        finally { LastImportDuration = clock.Elapsed; _importGate.Release(); }
    }

    // =========================================================================
    // Leitura e Mapeamento de CSV (Suporte a UTF-8, acentos e RFC 4180)
    // =========================================================================

    public ParsedCsvDocument ParseCsvFile(string filePath)
    {
        var content = ReadFileWithEncodingDetection(filePath);
        return ParseCsvContent(content);
    }

    public ParsedCsvDocument ParseCsvContent(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent))
            return new ParsedCsvDocument();

        var delimiter = DetectCsvDelimiter(csvContent);
        var allRows = ParseCsvRecords(csvContent, delimiter);
        if (allRows.Count == 0)
            return new ParsedCsvDocument { Delimiter = delimiter };

        var headers = allRows[0].Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Coluna {idx + 1}" : h.Trim()).ToList();
        var dataRows = allRows.Skip(1).Where(r => r.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
        var mapping = SuggestColumnMapping(headers);

        return new ParsedCsvDocument
        {
            Delimiter = delimiter,
            Headers = headers,
            Rows = dataRows,
            SuggestedMapping = mapping
        };
    }

    public static CsvColumnMapping SuggestColumnMapping(IReadOnlyList<string> headers)
    {
        var map = new CsvColumnMapping();
        for (var i = 0; i < headers.Count; i++)
        {
            var h = NormalizeHeaderName(headers[i]);
            switch (h)
            {
                case "nome" or "name" or "identificacao" or "servidor" or "server" or "title" or "titulo":
                    if (map.NameColumnIndex < 0) map.NameColumnIndex = i;
                    break;
                case "cliente" or "client" or "empresa" or "company" or "customer" or "organizacao":
                    if (map.ClientColumnIndex < 0) map.ClientColumnIndex = i;
                    break;
                case "grupo" or "group" or "pasta" or "folder" or "categoria":
                    if (map.GroupColumnIndex < 0) map.GroupColumnIndex = i;
                    break;
                case "ambiente" or "environment" or "env":
                    if (map.EnvironmentColumnIndex < 0) map.EnvironmentColumnIndex = i;
                    break;
                case "protocolo" or "protocol" or "tipo" or "type":
                    if (map.ProtocolColumnIndex < 0) map.ProtocolColumnIndex = i;
                    break;
                case "host" or "hostname" or "ip" or "endereco" or "address" or "destino":
                    if (map.HostColumnIndex < 0) map.HostColumnIndex = i;
                    break;
                case "porta" or "port":
                    if (map.PortColumnIndex < 0) map.PortColumnIndex = i;
                    break;
                case "usuario" or "user" or "username" or "login":
                    if (map.UsernameColumnIndex < 0) map.UsernameColumnIndex = i;
                    break;
                case "dominio" or "domain":
                    if (map.DomainColumnIndex < 0) map.DomainColumnIndex = i;
                    break;
                case "chaveprivada" or "privatekey" or "privatekeypath" or "chave" or "keypath":
                    if (map.PrivateKeyPathColumnIndex < 0) map.PrivateKeyPathColumnIndex = i;
                    break;
                case "vpnnome" or "vpnname" or "nomevpn" or "nomedavpn":
                    if (map.VpnNameColumnIndex < 0) map.VpnNameColumnIndex = i;
                    break;
                case "vpnapp" or "vpnaplicativo" or "aplicativovpn" or "vpnclient" or "vpncliente":
                    if (map.VpnAppColumnIndex < 0) map.VpnAppColumnIndex = i;
                    break;
                case "vpnservidor" or "vpnserver" or "servidorvpn" or "vpnhost" or "vpnip" or "servidorip":
                    if (map.VpnServerColumnIndex < 0) map.VpnServerColumnIndex = i;
                    break;
                case "vpnobservacoes" or "vpnnotes" or "observacoesvpn" or "vpnobs" or "observacoesdavpn":
                    if (map.VpnNotesColumnIndex < 0) map.VpnNotesColumnIndex = i;
                    break;
                case "tags" or "tag" or "etiquetas":
                    if (map.TagsColumnIndex < 0) map.TagsColumnIndex = i;
                    break;
                case "observacoes" or "observacao" or "notes" or "descricao" or "comentarios":
                    if (map.NotesColumnIndex < 0) map.NotesColumnIndex = i;
                    break;
                case "favorito" or "favorite" or "isfavorite":
                    if (map.FavoriteColumnIndex < 0) map.FavoriteColumnIndex = i;
                    break;
            }
        }
        return map;
    }

    public ImportPreviewResult BuildCsvPreview(
        ParsedCsvDocument doc,
        CsvColumnMapping mapping,
        IReadOnlyList<ConnectionEntry> existingConnections)
    {
        var candidates = new List<ConnectionEntry>();
        foreach (var row in doc.Rows)
        {
            var protoStr = GetCell(row, mapping.ProtocolColumnIndex);
            var proto = EnumDisplayExtensions.ParseProtocol(protoStr);

            var portStr = GetCell(row, mapping.PortColumnIndex);
            var port = string.IsNullOrWhiteSpace(portStr)
                ? proto.GetDefaultPort()
                : (int.TryParse(portStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) ? parsedPort : -1);

            var favStr = GetCell(row, mapping.FavoriteColumnIndex).ToLowerInvariant();
            var isFav = favStr is "sim" or "true" or "1" or "yes" or "s";

            var client = GetCell(row, mapping.ClientColumnIndex);
            if (string.IsNullOrWhiteSpace(client))
                client = "Geral";

            var group = GetCell(row, mapping.GroupColumnIndex);
            if (string.IsNullOrWhiteSpace(group))
                group = "Geral";

            var host = GetCell(row, mapping.HostColumnIndex);
            var name = GetCell(row, mapping.NameColumnIndex);
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
                name = host;

            candidates.Add(new ConnectionEntry
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = name,
                ClientName = client,
                GroupName = group,
                Environment = EnumDisplayExtensions.ParseEnvironment(GetCell(row, mapping.EnvironmentColumnIndex)),
                Protocol = proto,
                Host = host,
                Port = port,
                Username = GetCell(row, mapping.UsernameColumnIndex),
                Domain = GetCell(row, mapping.DomainColumnIndex),
                PrivateKeyPath = GetCell(row, mapping.PrivateKeyPathColumnIndex),
                VpnName = GetCell(row, mapping.VpnNameColumnIndex),
                VpnApp = GetCell(row, mapping.VpnAppColumnIndex),
                VpnServer = GetCell(row, mapping.VpnServerColumnIndex),
                VpnNotes = GetCell(row, mapping.VpnNotesColumnIndex),
                Tags = GetCell(row, mapping.TagsColumnIndex),
                Notes = GetCell(row, mapping.NotesColumnIndex),
                IsFavorite = isFav
            });
        }

        return BuildPreviewFromCandidates(candidates, existingConnections, L.Format("CSV ({0} linhas)", (doc.Rows.Count)));
    }

    // =========================================================================
    // Leitura e Prévia de JSON e AutoPuTTY
    // =========================================================================

    public ImportPreviewResult ParseJsonPreview(string filePath, IReadOnlyList<ConnectionEntry> existingConnections)
    {
        var content = ReadFileWithEncodingDetection(filePath);
        return ParseJsonContentPreview(content, existingConnections);
    }

    public ImportPreviewResult ParseJsonContentPreview(string jsonContent, IReadOnlyList<ConnectionEntry> existingConnections)
    {
        List<ConnectionEntry>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<ConnectionEntry>>(jsonContent, JsonOptions);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or System.Text.Json.JsonException)
        {
            _logger.RecordFailure("Import.JSON", ex);
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("JSON Inválido"),
                DiagnosticMessage = L.Text("Não foi possível interpretar o arquivo JSON. Consulte o diagnóstico.")
            };
        }

        if (list == null)
        {
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("JSON Vazio"),
                DiagnosticMessage = L.Text("Nenhum registro encontrado no arquivo JSON.")
            };
        }

        ImportLimits.CheckCount(list.Count);
        foreach (var item in list)
        {
            if (item == null) throw new InvalidDataException(L.Text("Registro inválido"));
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("D");
        }

        return BuildPreviewFromCandidates(list, existingConnections, L.Format("JSON ({0} registros)", (list.Count)));
    }

    /// <summary>
    /// Avalia um arquivo de exportação do AutoPuTTY (.xml, .txt, .dat, .csv).
    /// Não presume a estrutura de arquivos criptografados; se detectar criptografia ou binário proprietário,
    /// apresenta diagnóstico claro ao usuário. Se for XML/INI/Texto plano do AutoPuTTY, extrai os servidores.
    /// </summary>
    public ImportPreviewResult EvaluateAndParseAutoPuttyFile(string filePath, IReadOnlyList<ConnectionEntry> existingConnections)
    {
        ImportLimits.CheckFile(filePath);
        var rawBytes = File.ReadAllBytes(filePath);
        if (rawBytes.Length == 0)
        {
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("AutoPuTTY (Vazio)"),
                DiagnosticMessage = L.Text("O arquivo selecionado está vazio.")
            };
        }

        // Verifica se contém bytes nulos / entropia binária típica de arquivo criptografado
        var nonTextCount = rawBytes.Take(512).Count(b => b < 9 || (b > 13 && b < 32));
        if (nonTextCount > 10)
        {
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("AutoPuTTY (Criptografado / Binário)"),
                DiagnosticMessage = L.Text("O arquivo selecionado aparenta estar criptografado ou em formato binário proprietário do AutoPuTTY. ") +
                                    L.Text("Por segurança e confiabilidade, o ConectaTI não tenta quebrar arquivos criptografados proprietários. ") +
                                    "Exporte a lista sem criptografia (XML ou CSV) no AutoPuTTY e importe novamente."
            };
        }

        var text = ReadFileWithEncodingDetection(filePath).Trim();

        // Se for XML (AutoPuTTY ou genérico), direciona para o parser XML completo
        if (text.StartsWith('<'))
        {
            return ParseXmlPreview(filePath, existingConnections);
        }

        // Caso 2: Arquivo INI / Seções [Servidor]
        if (text.Contains('[') && text.Contains(']') && text.Contains('='))
        {
            var candidates = ParseIniStyleAutoPutty(text);
            if (candidates.Count > 0)
            {
                return BuildPreviewFromCandidates(candidates, existingConnections, L.Format("AutoPuTTY INI ({0} registros)", (candidates.Count)));
            }
        }

        return new ImportPreviewResult
        {
            SourceFormat = L.Text("Formato AutoPuTTY Não Reconhecido"),
            DiagnosticMessage = L.Text("O arquivo não pôde ser reconhecido automaticamente como lista do AutoPuTTY. ") +
                                L.Text("Verifique se o arquivo está protegido por senha ou utilize a importação CSV com mapeamento de colunas.")
        };
    }

    public ImportPreviewResult ParseXmlPreview(string filePath, IReadOnlyList<ConnectionEntry> existingConnections, string? password = null)
    {
        var text = ReadFileWithEncodingDetection(filePath).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("XML Vazio"),
                DiagnosticMessage = L.Text("O arquivo XML selecionado está vazio.")
            };
        }

        // Caso 1: Formato XML próprio do ConectaTI
        if (text.Contains("<ConectaTI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var doc = ImportLimits.ReadXml(text);
                var candidates = new List<ConnectionEntry>();
                foreach (var node in doc.Descendants().Where(e => e.Name.LocalName.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
                {
                    AutoPuttyCodec.CheckReadProgress(candidates.Count + 1);
                    var name = GetXmlValue(node, "Name");
                    var client = GetXmlValue(node, "Client", "ClientName");
                    var group = GetXmlValue(node, "Group", "GroupName");
                    var protoStr = GetXmlValue(node, "Protocol");
                    var proto = EnumDisplayExtensions.ParseProtocol(protoStr);
                    var sshPrefStr = GetXmlValue(node, "SshPreference");
                    var sshPref = Enum.TryParse<SshClientPreference>(sshPrefStr, true, out var sp) ? sp : SshClientPreference.Putty;
                    var host = GetXmlValue(node, "Host");
                    var portStr = GetXmlValue(node, "Port");
                    var port = int.TryParse(portStr, out var p) ? p : proto.GetDefaultPort();
                    var user = GetXmlValue(node, "Username", "User");
                    var domain = GetXmlValue(node, "Domain");
                    var keyPath = GetXmlValue(node, "PrivateKeyPath", "PrivateKey");
                    var vpnName = GetXmlValue(node, "VpnName", "VPNName", "Vpn");
                    var vpnApp = GetXmlValue(node, "VpnApp", "VPNApp", "VpnApplication", "AplicativoVpn");
                    var vpnServer = GetXmlValue(node, "VpnServer", "VPNServer", "VpnHost", "ServidorVpn");
                    var vpnNotes = GetXmlValue(node, "VpnNotes", "VPNNotes", "ObservacoesVpn");
                    var tags = GetXmlValue(node, "Tags");
                    var notes = GetXmlValue(node, "Notes");
                    var favStr = GetXmlValue(node, "IsFavorite");
                    var isFav = bool.TryParse(favStr, out var fav) && fav;
                    var envStr = GetXmlValue(node, "Environment");
                    var env = EnumDisplayExtensions.ParseEnvironment(envStr);

                    candidates.Add(new ConnectionEntry
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Name = string.IsNullOrWhiteSpace(name) ? host : name,
                        ClientName = string.IsNullOrWhiteSpace(client) ? "Geral" : client,
                        GroupName = string.IsNullOrWhiteSpace(group) ? "Geral" : group,
                        Environment = env,
                        Protocol = proto,
                        SshClient = sshPref,
                        Host = host,
                        Port = port,
                        Username = user,
                        Domain = domain,
                        PrivateKeyPath = keyPath,
                        VpnName = vpnName,
                        VpnApp = vpnApp,
                        VpnServer = vpnServer,
                        VpnNotes = vpnNotes,
                        Tags = tags,
                        Notes = notes,
                        IsFavorite = isFav
                    });
                }

                return BuildPreviewFromCandidates(candidates, existingConnections, L.Format("ConectaTI XML ({0} conexões)", (candidates.Count)));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException)
            {
                _logger.RecordFailure("Import.XML", ex);
                return new ImportPreviewResult
                {
                    SourceFormat = L.Text("ConectaTI XML Inválido"),
                    DiagnosticMessage = L.Text("Não foi possível interpretar o XML AutoPuTTY. Consulte o diagnóstico.")
                };
            }
        }

        // Caso 2: Formato XML do AutoPuTTY (com descriptografia real de <List> e campos)
        if (AutoPuttyCodec.IsAutoPuttyXml(text))
        {
            try
            {
                var records = AutoPuttyCodec.ParseAutoPuttyXml(text, password);
                if (records.Count > 0)
                {
                    var known = existingConnections.GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
                    var items = new List<ImportPreviewItem>();
                    for (var i = 0; i < records.Count; i++)
                    {
                        AutoPuttyCodec.CheckReadProgress(i + 1);
                        var r = records[i];
                        var entry = new ConnectionEntry
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            Name = r.Name,
                            ClientName = "Geral",
                            GroupName = string.IsNullOrWhiteSpace(r.Group) ? "Geral" : r.Group,
                            Environment = EnvironmentType.Production,
                            Protocol = r.Protocol,
                            SshClient = r.SshPreference,
                            Host = r.Host,
                            Port = r.Port,
                            Username = r.Username,
                            Domain = r.Domain,
                            PrivateKeyPath = r.PrivateKeyPath,
                            Notes = string.Empty
                        };

                        var validationErrors = ConnectionValidator.ValidateConnection(entry);
                        var isValid = validationErrors.Count == 0;

                        known.TryGetValue(Identity(entry), out var duplicate);
                        known.TryAdd(Identity(entry), entry);

                        var status = !isValid
                            ? L.Format("Inválido: {0}", (L.JoinMessages(validationErrors)))
                            : (duplicate != null ? L.Text("Duplicado (Cliente + Nome + Host:Porta já existe)") : L.Text("Pronto para importar"));

                        items.Add(new ImportPreviewItem
                        {
                            RowNumber = i + 1,
                            Entry = entry,
                            IsValid = isValid,
                            IsDuplicate = duplicate != null,
                            ExistingConnectionId = duplicate?.Id,
                            StatusText = status,
                            IsSelected = isValid,
                            RecoveredPlainPassword = r.PlainPassword,
                            UnrecoveredFields = r.PasswordUnreadable ? L.Text("Senha") : string.Empty
                        });
                    }

                    var summary = L.Format("AutoPuTTY XML reconhecido: {0} conexões | Válidas: {1} | Com senha recuperada: {2}", (items.Count), (items.Count(x => x.IsValid)), (records.Count(x => x.PasswordRecovered)));
                    return new ImportPreviewResult
                    {
                        SourceFormat = L.Format("AutoPuTTY XML ({0} conexões)", (records.Count)),
                        DiagnosticMessage = summary,
                        Items = items
                    };
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException)
            {
                _logger.RecordFailure("Import.Decode", ex);
                return new ImportPreviewResult { SourceFormat = L.Text("AutoPuTTY XML Inválido"), DiagnosticMessage = L.Text("Não foi possível interpretar o XML AutoPuTTY. Consulte o diagnóstico.") };
            }
        }

        // Caso 3: Fallback para XML com tags padrão
        try
        {
            var xdoc = ImportLimits.ReadXml(text);
            var candidates = new List<ConnectionEntry>();

            foreach (var node in xdoc.Descendants().Where(e =>
                e.Name.LocalName.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("Session", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("Item", StringComparison.OrdinalIgnoreCase)))
            {
                var host = GetXmlValue(node, "Host", "Hostname", "IP", "Address", "ServerName");
                var name = GetXmlValue(node, "Name", "DisplayName", "Title", "Description");
                if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(name))
                    continue;

                var group = GetXmlValue(node, "Group", "Folder", "Category", "Parent");
                var client = GetXmlValue(node, "Client", "Company", "Customer");
                var protoStr = GetXmlValue(node, "Protocol", "Type", "ConnectionType");
                var proto = EnumDisplayExtensions.ParseProtocol(protoStr);
                var portStr = GetXmlValue(node, "Port");
                var port = int.TryParse(portStr, out var p) ? p : proto.GetDefaultPort();
                var user = GetXmlValue(node, "Username", "User", "Login");
                var domain = GetXmlValue(node, "Domain");
                var keyPath = GetXmlValue(node, "PrivateKey", "KeyFile", "PrivateKeyPath");
                var vpnName = GetXmlValue(node, "VpnName", "VPNName", "Vpn");
                var vpnApp = GetXmlValue(node, "VpnApp", "VPNApp", "VpnClient");
                var vpnServer = GetXmlValue(node, "VpnServer", "VPNServer", "VpnHost");
                var vpnNotes = GetXmlValue(node, "VpnNotes", "VPNNotes");

                candidates.Add(new ConnectionEntry
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = string.IsNullOrWhiteSpace(name) ? host : name,
                    ClientName = string.IsNullOrWhiteSpace(client) ? (string.IsNullOrWhiteSpace(group) ? "Importado" : group) : client,
                    GroupName = string.IsNullOrWhiteSpace(group) ? "Geral" : group,
                    Environment = EnvironmentType.Production,
                    Protocol = proto,
                    Host = host,
                    Port = port,
                    Username = user,
                    Domain = domain,
                    PrivateKeyPath = keyPath,
                    VpnName = vpnName,
                    VpnApp = vpnApp,
                    VpnServer = vpnServer,
                    VpnNotes = vpnNotes,
                    Notes = string.Empty
                });
            }

            if (candidates.Count > 0)
            {
                return BuildPreviewFromCandidates(candidates, existingConnections, L.Format("XML ({0} conexões)", (candidates.Count)));
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            _logger.RecordFailure("Import.XML", ex);
            return new ImportPreviewResult
            {
                SourceFormat = L.Text("XML Inválido"),
                DiagnosticMessage = L.Text("Não foi possível interpretar o XML AutoPuTTY. Consulte o diagnóstico.")
            };
        }

        return new ImportPreviewResult
        {
            SourceFormat = L.Text("XML Não Reconhecido"),
            DiagnosticMessage = L.Text("O arquivo XML não contém elementos de conexão reconhecidos ou está protegido por uma senha não informada.")
        };
    }

    public ImportPreviewResult BuildPreviewFromCandidates(
        IReadOnlyList<ConnectionEntry> candidates,
        IReadOnlyList<ConnectionEntry> existingConnections,
        string sourceFormat)
    {
        ImportLimits.CheckCount(candidates.Count);
        var known = existingConnections.GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
        var items = new List<ImportPreviewItem>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            AutoPuttyCodec.CheckReadProgress(i + 1);
            var c = candidates[i];
            ImportLimits.CheckEntry(c);
            var validationErrors = ConnectionValidator.ValidateConnection(c);
            var isValid = validationErrors.Count == 0;

            known.TryGetValue(Identity(c), out var duplicate);
            known.TryAdd(Identity(c), c);

            var status = !isValid
                ? L.Format("Inválido: {0}", (L.JoinMessages(validationErrors)))
                : (duplicate != null ? L.Text("Duplicado (Cliente + Nome + Host:Porta já existe)") : L.Text("Pronto para importar"));

            items.Add(new ImportPreviewItem
            {
                RowNumber = i + 1,
                Entry = c,
                IsValid = isValid,
                IsDuplicate = duplicate != null,
                ExistingConnectionId = duplicate?.Id,
                StatusText = status
            });
        }

        var summary = L.Format("Total: {0} | Válidos: {1} | Duplicados: {2} | Inválidos: {3}", (items.Count), (items.Count(x => x.IsValid)), (items.Count(x => x.IsValid && x.IsDuplicate)), (items.Count(x => !x.IsValid)));
        return new ImportPreviewResult
        {
            SourceFormat = sourceFormat,
            DiagnosticMessage = summary,
            Items = items
        };
    }

    // =========================================================================
    // Helpers internos de CSV / XML / INI / Encoding
    // =========================================================================

    private static string ReadFileWithEncodingDetection(string filePath)
    {
        ImportLimits.CheckFile(filePath);
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            var utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            // Fallback para Windows-1252 / ISO-8859-1 caso o CSV tenha sido salvo em ANSI no Excel em português
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static char DetectCsvDelimiter(string content)
    {
        var firstLineEnd = content.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd >= 0 ? content[..firstLineEnd] : content;
        var semicolons = firstLine.Count(c => c == ';');
        var commas = firstLine.Count(c => c == ',');
        return semicolons >= commas ? ';' : ',';
    }

    public static List<string[]> ParseCsvRecords(string content, char delimiter)
    {
        if (content.Length > ImportLimits.MaxFileBytes) throw new InvalidDataException(L.Text("O arquivo excede o limite de 32 MB."));
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            if (i % 4096 == 0) AutoPuttyCodec.CheckReadProgress(Math.Max(0, rows.Count - 1));
            if (currentCell.Length > ImportLimits.MaxFieldCharacters || currentRow.Count > 256) throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                        i++;

                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                    rows.Add(currentRow.ToArray());
                    ImportLimits.CheckCount(Math.Max(0, rows.Count - 1));
                    currentRow = new List<string>();
                }
                else
                {
                    currentCell.Append(c);
                }
            }
        }

        if (currentCell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentCell.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    private static string EscapeCsvField(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Contains(';') || s.Contains(',') || s.Contains('"') || s.Contains('\r') || s.Contains('\n'))
        {
            return $"\"{s.Replace("\"", "\"\"")}\"";
        }
        return s;
    }

    private static string GetCell(string[] row, int index)
    {
        if (index < 0 || index >= row.Length)
            return string.Empty;
        return row[index]?.Trim() ?? string.Empty;
    }

    private static string NormalizeHeaderName(string header)
    {
        var formD = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string GetXmlValue(XElement element, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            var attr = element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attr != null && !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value.Trim();

            var child = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child != null && !string.IsNullOrWhiteSpace(child.Value))
                return child.Value.Trim();
        }
        return string.Empty;
    }

    private static List<ConnectionEntry> ParseIniStyleAutoPutty(string text)
    {
        var list = new List<ConnectionEntry>();
        ConnectionEntry? current = null;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                if (current != null && !string.IsNullOrWhiteSpace(current.Host))
                    list.Add(current);

                var sectionName = line[1..^1].Trim();
                current = new ConnectionEntry
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = sectionName,
                    ClientName = "Importado AutoPuTTY",
                    GroupName = "Geral"
                };
                continue;
            }

            if (current == null)
                continue;

            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var key = line[..eqIdx].Trim().ToLowerInvariant();
            var val = line[(eqIdx + 1)..].Trim();

            switch (key)
            {
                case "host" or "hostname" or "ip" or "address":
                    current.Host = val;
                    break;
                case "port" or "porta":
                    if (int.TryParse(val, out var p)) current.Port = p;
                    break;
                case "protocol" or "type" or "protocolo":
                    current.Protocol = EnumDisplayExtensions.ParseProtocol(val);
                    if (current.Port == 3389 && current.Protocol != ConnectionProtocol.Rdp)
                        current.Port = current.Protocol.GetDefaultPort();
                    break;
                case "user" or "username" or "login":
                    current.Username = val;
                    break;
                case "group" or "folder" or "pasta":
                    current.GroupName = val;
                    break;
                case "client" or "cliente":
                    current.ClientName = val;
                    break;
            }
        }

        if (current != null && !string.IsNullOrWhiteSpace(current.Host))
            list.Add(current);

        return list;
    }
}
