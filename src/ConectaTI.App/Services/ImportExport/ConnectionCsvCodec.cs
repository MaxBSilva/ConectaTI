using System.Globalization;
using System.Text;
using ConectaTI.App.Models;
using static ConectaTI.App.Services.ImportExport.ConnectionTransferValues;
namespace ConectaTI.App.Services.ImportExport;

internal static class ConnectionCsvCodec
{
    public static string Write(IEnumerable<ConnectionEntry> connections)
    {
            var sb = new StringBuilder();
            var headers = new[]
            {
                "Nome", "Cliente", "Grupo", "Ambiente", "Protocolo",
                "Host", "Porta", "Usuario", "Dominio", "ChavePrivada",
                "VpnNome", "VpnAplicativo", "VpnServidor", "VpnUsuario", "VpnObservacoes",
                "Tags", "Observacoes", "Favorito", "ConfiguracaoRdp", "Ativo", "SshClient", "SshUseAgent", "VncParameters"
            };
            sb.AppendLine(string.Join(";", headers.Select(CsvRecordCodec.Escape)));

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
                    c.VpnUsername,
                    c.VpnNotes,
                    c.Tags,
                    c.Notes,
                    c.IsFavorite ? "Sim" : "Não",
                    RdpFileService.ExportMetadata(c),
                    c.IsActive ? "Sim" : "Não",
                    c.SshClient.ToString(), c.SshUseAgent ? "true" : "false", c.VncParameters
                };
                sb.AppendLine(string.Join(";", row.Select(CsvRecordCodec.Escape)));
            }


        return sb.ToString();
    }
    public static ParsedCsvDocument Parse(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent))
            return new ParsedCsvDocument();

        var delimiter = CsvRecordCodec.DetectDelimiter(csvContent);
        var allRows = CsvRecordCodec.Parse(csvContent, delimiter);
        if (allRows.Count == 0)
            return new ParsedCsvDocument { Delimiter = delimiter };

        var headers = allRows[0].Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Coluna {idx + 1}" : h.Trim()).ToList();
        var dataRows = allRows.Skip(1).Where(r => r.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
        var mapping = SuggestMapping(headers);

        return new ParsedCsvDocument
        {
            Delimiter = delimiter,
            Headers = headers,
            Rows = dataRows,
            SuggestedMapping = mapping
        };
    }


    public static CsvColumnMapping SuggestMapping(IReadOnlyList<string> headers)
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
                case "vpnusuario" or "vpnusername" or "vpnuser" or "usuariovpn" or "usuariodavpn":
                    if (map.VpnUsernameColumnIndex < 0) map.VpnUsernameColumnIndex = i;
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
                case "sshclient" or "sshpreference": map.SshClientColumnIndex = i; break;
                case "sshuseagent": map.SshUseAgentColumnIndex = i; break;
                case "vncparameters": map.VncParametersColumnIndex = i; break;
                case "ativo" or "isactive" or "active":
                    map.ActiveColumnIndex = i; break;
                case "configuracaordp":
                    map.RdpConfigurationColumnIndex = i; break;
                case "favorito" or "favorite" or "isfavorite":
                    if (map.FavoriteColumnIndex < 0) map.FavoriteColumnIndex = i;
                    break;
            }
        }
        return map;
    }


    public static List<ConnectionEntry> ReadEntries(ParsedCsvDocument doc, CsvColumnMapping mapping)
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
                SshClient = ParseSshClient(GetCell(row, mapping.SshClientColumnIndex)),
                SshUseAgent = ParseAgent(GetCell(row, mapping.SshUseAgentColumnIndex)),
                VncParameters = GetCell(row, mapping.VncParametersColumnIndex),
                Host = host,
                Port = port,
                Username = GetCell(row, mapping.UsernameColumnIndex),
                Domain = GetCell(row, mapping.DomainColumnIndex),
                PrivateKeyPath = GetCell(row, mapping.PrivateKeyPathColumnIndex),
                VpnName = GetCell(row, mapping.VpnNameColumnIndex),
                VpnApp = GetCell(row, mapping.VpnAppColumnIndex),
                VpnServer = GetCell(row, mapping.VpnServerColumnIndex),
                VpnUsername = GetCell(row, mapping.VpnUsernameColumnIndex),
                VpnNotes = GetCell(row, mapping.VpnNotesColumnIndex),
                Tags = GetCell(row, mapping.TagsColumnIndex),
                Notes = GetCell(row, mapping.NotesColumnIndex),
                IsActive = ParseActive(GetCell(row, mapping.ActiveColumnIndex)),
                IsFavorite = isFav
            });
            RdpFileService.ApplyMetadata(candidates[^1], GetCell(row, mapping.RdpConfigurationColumnIndex));
        }


        return candidates;
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


}
