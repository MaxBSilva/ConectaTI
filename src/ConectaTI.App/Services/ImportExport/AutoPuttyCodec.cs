using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using Konscious.Security.Cryptography;

namespace ConectaTI.App.Services.ImportExport;

public record AutoPuttyServerRecord(
    string Name,
    string Host,
    int Port,
    string Username,
    string Domain,
    string PlainPassword,
    string PrivateKeyPath,
    ConnectionProtocol Protocol,
    SshClientPreference SshPreference,
    bool PasswordRecovered,
    string Group = "Geral", bool PasswordUnreadable = false);

/// <summary>
/// Implementa decodificação e serialização de arquivos XML compatíveis com o AutoPuTTY (versões legadas e modernas),
/// incluindo derivação de chaves via Argon2id (AES-CBC) e TripleDES (ECB com chave MD5).
/// </summary>
public static class AutoPuttyCodec
{
    private static readonly AsyncLocal<ReadScope?> CurrentRead = new();
    public static IDisposable BeginReadScope(CancellationToken token = default, IProgress<ImportProgress>? progress = null)
        => new ReadScope(token, progress);

    private sealed class ReadScope : IDisposable
    {
        private readonly ReadScope? _previous;
        internal readonly CancellationToken Token;
        internal readonly IProgress<ImportProgress>? Progress;
        internal readonly Dictionary<string, byte[]> Keys = new();
        public ReadScope(CancellationToken token, IProgress<ImportProgress>? progress)
        { _previous = CurrentRead.Value; Token = token; Progress = progress; CurrentRead.Value = this; }
        public void Dispose()
        {
            foreach (var key in Keys.Values) CryptographicOperations.ZeroMemory(key);
            Keys.Clear(); CurrentRead.Value = _previous;
        }
    }
    internal static void CheckReadProgress(int count)
    {
        CurrentRead.Value?.Token.ThrowIfCancellationRequested();
        ImportLimits.CheckCount(count);
        if (count % 25 == 0) CurrentRead.Value?.Progress?.Report(new("Validating", count));
    }
    internal static void CheckCancellation() => CurrentRead.Value?.Token.ThrowIfCancellationRequested();
    public const string DefaultInternalCryptoKey = "12ùMkldQ%kS2A";
    public const string DefaultLegacyCryptoKey = ";Sk/#µ^éD}=@|";

    private const int SaltSize = 16;
    private const int Parallelism = 16;

    /// <summary>
    /// Verifica se um texto XML representa a estrutura de um arquivo AutoPuTTY (<Data> com <List>, ou <List> com <Server>).
    /// </summary>
    public static bool IsAutoPuttyXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return false;

        var trimmed = xmlContent.Trim();
        if (!trimmed.StartsWith('<'))
            return false;

        return (trimmed.Contains("<Data", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<List", StringComparison.OrdinalIgnoreCase)) ||
               (trimmed.Contains("<List", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<Server", StringComparison.OrdinalIgnoreCase)) ||
               (trimmed.Contains("<AutoPuTTY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifica se o arquivo AutoPuTTY possui hash de senha mestra exigida.
    /// </summary>
    public static bool RequiresPassword(string xmlContent)
    {
        try
        {
            var doc = ImportLimits.ReadXml(xmlContent);
            var metadata = doc.Root?.Elements() ?? Enumerable.Empty<XElement>();
            var hashNode = metadata.FirstOrDefault(e => e.Name.LocalName.Equals("Hash", StringComparison.OrdinalIgnoreCase));
            if (hashNode != null && !string.IsNullOrWhiteSpace(hashNode.Value))
                return true;

            var passwordNode = metadata.FirstOrDefault(e =>
                e.Name.LocalName.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("passwordmd5", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("passwordpbk", StringComparison.OrdinalIgnoreCase));

            return passwordNode != null && !string.IsNullOrWhiteSpace(passwordNode.Value);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodifica e extrai conexões de um arquivo AutoPuTTY XML real.
    /// </summary>
    public static List<AutoPuttyServerRecord> ParseAutoPuttyXml(string xmlContent, string? optionalPassword = null)
    {
        using var scope = CurrentRead.Value == null ? BeginReadScope() : null;
        CurrentRead.Value?.Token.ThrowIfCancellationRequested();
        var results = new List<AutoPuttyServerRecord>();
        var doc = ImportLimits.ReadXml(xmlContent);

        // Caso 1: Estrutura <Data><List>...texto cifrado...</List></Data>
        var listNode = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("List", StringComparison.OrdinalIgnoreCase));
        if (listNode != null && !listNode.HasElements && !string.IsNullOrWhiteSpace(listNode.Value))
        {
            var encryptedBlob = listNode.Value.Trim();
            var decryptedXml = DecryptAutoPuttyString(encryptedBlob, optionalPassword);

            if (decryptedXml == encryptedBlob || !decryptedXml.TrimStart().StartsWith('<'))
                throw new CryptographicException(L.Text("Não foi possível decifrar a lista AutoPuTTY. Verifique a senha do arquivo."));

            if (!string.IsNullOrWhiteSpace(decryptedXml))
            {
                var innerWrapped = decryptedXml.Trim().StartsWith('<') && decryptedXml.Trim().StartsWith("<List", StringComparison.OrdinalIgnoreCase)
                    ? decryptedXml
                    : $"<List>{decryptedXml}</List>";

                return ParseDecryptedListXml(innerWrapped, optionalPassword);
            }
        }

        // Caso 2: Estrutura onde <List> já contém os nós <Server> diretamente
        return ParseDecryptedListXml(xmlContent, optionalPassword);
    }

    public static List<AutoPuttyServerRecord> ParseDecryptedListXml(string xmlContent, string? optionalPassword = null)
    {
        var records = new List<AutoPuttyServerRecord>();
        XDocument doc;
        try
        {
            doc = ImportLimits.ReadXml(xmlContent);
        }
        catch (System.Xml.XmlException)
        {
            // Tenta envolver em raiz se for múltiplos nós soltos
            doc = ImportLimits.ReadXml($"<Root>{xmlContent}</Root>");
        }

        // Mapeia eventuais <Vault Name="..."> para senhas/chaves compartilhadas
        var vaults = new Dictionary<string, (string Password, string PrivateKey)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("Vault", StringComparison.OrdinalIgnoreCase) && e.Attribute("Name") != null))
        {
            CurrentRead.Value?.Token.ThrowIfCancellationRequested();
            var vName = vNode.Attribute("Name")?.Value ?? string.Empty;
            var vPassEnc = vNode.Element("Password")?.Value ?? string.Empty;
            var vPrivEnc = vNode.Element("PrivateKey")?.Value ?? string.Empty;

            var vPass = TryDecryptField(vPassEnc, optionalPassword);
            var vPriv = TryDecryptField(vPrivEnc, optionalPassword);
            vaults[vName] = (vPass, vPriv);
        }

        // Extrai todos os servidores <Server Name="...">
        foreach (var sNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("Server", StringComparison.OrdinalIgnoreCase)))
        {
            CurrentRead.Value?.Token.ThrowIfCancellationRequested();
            ImportLimits.CheckCount(records.Count + 1);
            if (records.Count % 25 == 0) CurrentRead.Value?.Progress?.Report(new("Decoding", records.Count));
            var name = sNode.Attribute("Name")?.Value ?? sNode.Element("Name")?.Value ?? string.Empty;
            var hostRawEnc = sNode.Element("Host")?.Value ?? sNode.Attribute("Host")?.Value ?? string.Empty;
            var userRawEnc = sNode.Element("User")?.Value ?? sNode.Attribute("Username")?.Value ?? string.Empty;
            var passRawEnc = sNode.Element("Password")?.Value ?? sNode.Attribute("Password")?.Value ?? string.Empty;
            var privRawEnc = sNode.Element("PrivateKey")?.Value ?? sNode.Attribute("PrivateKey")?.Value ?? string.Empty;
            var typeRaw = sNode.Element("Type")?.Value ?? sNode.Attribute("Type")?.Value ?? sNode.Attribute("Protocol")?.Value ?? "1";
            var vaultRef = sNode.Element("Vault")?.Value?.Trim() ?? string.Empty;
            var group = sNode.Attribute("Group")?.Value ?? sNode.Element("Group")?.Value ?? "Geral";

            var hostDecrypted = TryDecryptField(hostRawEnc, optionalPassword);
            var userDecrypted = TryDecryptField(userRawEnc, optionalPassword);
            var passDecrypted = TryDecryptField(passRawEnc, optionalPassword);
            var privDecrypted = TryDecryptField(privRawEnc, optionalPassword);

            if (!string.IsNullOrEmpty(vaultRef) && vaults.TryGetValue(vaultRef, out var vaultData))
            {
                if (string.IsNullOrEmpty(passDecrypted)) passDecrypted = vaultData.Password;
                if (string.IsNullOrEmpty(privDecrypted)) privDecrypted = vaultData.PrivateKey;
            }

            var (protocol, sshPref, defaultPort) = MapAutoPuttyType(typeRaw);

            // Host & Porta
            var parsedHost = HostPortParser.Parse(hostDecrypted, protocol);
            var effectiveHost = parsedHost.CleanHost;
            var portAttrStr = sNode.Attribute("Port")?.Value ?? sNode.Element("Port")?.Value;
            var effectivePort = parsedHost.HasExplicitPort
                ? parsedHost.Port
                : (int.TryParse(portAttrStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var apPort) && apPort is >= 1 and <= 65535 ? apPort : defaultPort);

            // Usuário & Domínio
            var parsedUser = UserIdentityFormatter.Normalize(userDecrypted, null);

            records.Add(new AutoPuttyServerRecord(
                Name: string.IsNullOrWhiteSpace(name) ? effectiveHost : name,
                Host: effectiveHost,
                Port: effectivePort,
                Username: parsedUser.CleanUsername,
                Domain: parsedUser.Domain,
                PlainPassword: passDecrypted,
                PrivateKeyPath: privDecrypted,
                Protocol: protocol,
                SshPreference: sshPref,
                PasswordRecovered: !string.IsNullOrEmpty(passDecrypted),
                Group: group, PasswordUnreadable: !string.IsNullOrEmpty(passRawEnc) && string.IsNullOrEmpty(passDecrypted)));
        }

        return records;
    }

    public static (ConnectionProtocol Protocol, SshClientPreference SshPref, int DefaultPort) MapAutoPuttyType(string? typeStr)
    {
        var raw = (typeStr ?? string.Empty).Trim().ToLowerInvariant();

        // No AutoPuTTY clássico:
        // 0: PuTTY (SSH)
        // 1: Remote Desktop (MSTSC/RDP)
        // 2: VNC
        // 3: WinSCP (SCP)
        // 4: WinSCP (SFTP)
        // 5: WinSCP (FTP)
        switch (raw)
        {
            case "0" or "putty" or "ssh":
                return (ConnectionProtocol.Ssh, SshClientPreference.Putty, 22);
            case "1" or "remote desktop" or "rdp" or "mstsc":
                return (ConnectionProtocol.Rdp, SshClientPreference.Putty, 3389);
            case "2" or "vnc":
                return (ConnectionProtocol.Vnc, SshClientPreference.Putty, 5900);
            case "3" or "winscp (scp)" or "scp":
                return (ConnectionProtocol.Scp, SshClientPreference.Putty, 22);
            case "4" or "winscp (sftp)" or "sftp":
                return (ConnectionProtocol.Sftp, SshClientPreference.Putty, 22);
            case "5" or "winscp (ftp)" or "ftp":
                return (ConnectionProtocol.Sftp, SshClientPreference.Putty, 21);
            case "openssh" or "ssh — openssh":
                return (ConnectionProtocol.Ssh, SshClientPreference.OpenSsh, 22);
            default:
                return (ConnectionProtocol.Rdp, SshClientPreference.Putty, 3389);
        }
    }

    public static string FormatAutoPuttyType(ConnectionProtocol protocol, SshClientPreference sshPref)
    {
        return protocol switch
        {
            ConnectionProtocol.Ssh when sshPref == SshClientPreference.Putty => "0",
            ConnectionProtocol.Rdp => "1",
            ConnectionProtocol.Vnc => "2",
            ConnectionProtocol.Scp => "3",
            ConnectionProtocol.Sftp => "4",
            ConnectionProtocol.Ssh => "0",
            _ => "1"
        };
    }

    /// <summary>
    /// Descriptografa uma string encriptada pelo AutoPuTTY, tentando:
    /// 1. AES CBC com Argon2id ($v2$ ou $v1$).
    /// 2. TripleDES ECB com MD5.
    /// Utiliza chaves na ordem: senha informada pelo usuário, chave padrão clássica, chave legada.
    /// </summary>
    public static string DecryptAutoPuttyString(string encrypted, string? optionalPassword = null)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
            return string.Empty;

        var clean = encrypted.Trim();
        CurrentRead.Value?.Token.ThrowIfCancellationRequested();
        var keysToTry = new List<string>();
        if (!string.IsNullOrWhiteSpace(optionalPassword)) keysToTry.Add(optionalPassword);
        keysToTry.Add(DefaultInternalCryptoKey);
        keysToTry.Add(DefaultLegacyCryptoKey);

        // Se começa com $v2$ ou $v1$, formato Argon2 + AES
        if (clean.StartsWith("$v2$", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("$v1$", StringComparison.OrdinalIgnoreCase))
        {
            var rawCipher = Regex.Replace(clean, @"^\$v\d+\$", "");
            foreach (var key in keysToTry)
            {
                try
                {
                    var decrypted = DecryptAesArgon2(rawCipher, key);
                    if (!string.IsNullOrWhiteSpace(decrypted))
                        return decrypted;
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
                {
                    // Continua tentando
                }
            }
        }

        // Tenta TripleDES legado
        foreach (var key in keysToTry)
        {
            try
            {
                var decrypted = DecryptTripleDes(clean, key);
                if (!string.IsNullOrWhiteSpace(decrypted))
                    return decrypted;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Continua tentando
            }
        }

        if (clean.StartsWith("$v", StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException(L.Text("Campo AutoPuTTY criptografado ilegível."));
        return clean;
    }

    private static string TryDecryptField(string value, string? optionalPassword)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Se contiver caracteres não Base64 ou espaços normais de texto plano, retorna como está
        if (!value.TrimStart().StartsWith("$v", StringComparison.OrdinalIgnoreCase) &&
            (value.Contains(' ') || value.Contains('<') || value.Contains('\\')))
            return value;

        try
        {
            var decrypted = DecryptAutoPuttyString(value, optionalPassword);
            return !string.IsNullOrWhiteSpace(decrypted) ? decrypted : value;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Descriptografa com TripleDES ECB (algoritmo legado original do AutoPuTTY).
    /// </summary>
    public static string DecryptTripleDes(string base64Encrypted, string key)
    {
        if (string.IsNullOrEmpty(base64Encrypted))
            return string.Empty;

        var encryptedBytes = Convert.FromBase64String(base64Encrypted);
        using var md5 = MD5.Create();
        var keyArray = md5.ComputeHash(Encoding.UTF8.GetBytes(key));

        using var tripleDes = TripleDES.Create();
        tripleDes.Key = keyArray;
        tripleDes.Mode = CipherMode.ECB;
        tripleDes.Padding = PaddingMode.PKCS7;

        using var decryptor = tripleDes.CreateDecryptor();
        var result = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return new UTF8Encoding(false, true).GetString(result);
    }

    /// <summary>
    /// Criptografa com TripleDES ECB (compatível com AutoPuTTY legado).
    /// </summary>
    public static string EncryptTripleDes(string plainText, string key)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        using var md5 = MD5.Create();
        var keyArray = md5.ComputeHash(Encoding.UTF8.GetBytes(key));

        using var tripleDes = TripleDES.Create();
        tripleDes.Key = keyArray;
        tripleDes.Mode = CipherMode.ECB;
        tripleDes.Padding = PaddingMode.PKCS7;

        using var encryptor = tripleDes.CreateEncryptor();
        var result = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Descriptografa com Argon2id + AES-CBC (versão moderna AutoPuTTY v2).
    /// </summary>
    public static string DecryptAesArgon2(string base64Encrypted, string password)
    {
        var cipherData = Convert.FromBase64String(base64Encrypted);
        if (cipherData.Length < SaltSize * 2)
            return string.Empty;

        var salt = new byte[SaltSize];
        var iv = new byte[SaltSize];
        Array.Copy(cipherData, 0, salt, 0, salt.Length);
        Array.Copy(cipherData, salt.Length, iv, 0, iv.Length);

        CurrentRead.Value?.Token.ThrowIfCancellationRequested();
        // Cache only within this read, with a bounded count. Parameters must remain compatible.
        var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + password)));
        byte[] key;
        var cached = CurrentRead.Value?.Keys.TryGetValue(cacheId, out key!) == true;
        if (!cached)
        {
            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            { Salt = salt, DegreeOfParallelism = Parallelism, Iterations = 4, MemorySize = 64 * 1024 };
            key = argon2.GetBytes(32);
            if (CurrentRead.Value is { } context && context.Keys.Count < 64) context.Keys.Add(cacheId, key);
        }
        else key = CurrentRead.Value!.Keys[cacheId];

        try
        {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        var ciphertextOffset = salt.Length + iv.Length;
        var ciphertextLength = cipherData.Length - ciphertextOffset;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
        {
            cs.Write(cipherData, ciphertextOffset, ciphertextLength);
            cs.FlushFinalBlock();
        }

        var result = new UTF8Encoding(false, true).GetString(ms.ToArray());
        return result;
        }
        finally
        {
            if (CurrentRead.Value == null || !CurrentRead.Value.Keys.ContainsKey(cacheId)) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Gera um arquivo XML no formato nativo do AutoPuTTY.
    /// </summary>
    public static string GenerateCompatibleAutoPuttyXml(IEnumerable<ConnectionEntry> connections, string key = DefaultInternalCryptoKey)
    {
        var sbList = new StringBuilder();
        foreach (var conn in connections)
        {
            var hostPort = HostPortParser.FormatDisplay(conn.Host, conn.Port, conn.Protocol);
            var userDomain = UserIdentityFormatter.FormatDisplay(conn.Username, conn.Domain);
            var typeCode = FormatAutoPuttyType(conn.Protocol, conn.SshClient);

            var encHost = EncryptTripleDes(hostPort, key);
            var encUser = EncryptTripleDes(userDomain, key);
            var encPriv = EncryptTripleDes(conn.PrivateKeyPath, key);

            sbList.Append($"<Server Name=\"{System.Security.SecurityElement.Escape(conn.Name)}\">");
            sbList.Append($"<Host>{encHost}</Host>");
            sbList.Append($"<User>{encUser}</User>");
            sbList.Append("<Password></Password>");
            sbList.Append("<Vault></Vault>");
            sbList.Append($"<PrivateKey>{encPriv}</PrivateKey>");
            sbList.Append($"<Type>{typeCode}</Type>");
            sbList.Append("</Server>");
        }

        var innerList = sbList.ToString();
        var encList = EncryptTripleDes(innerList, key);

        var sbFinal = new StringBuilder();
        sbFinal.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sbFinal.AppendLine("<Data>");
        sbFinal.AppendLine("  <Position>100,100</Position>");
        sbFinal.AppendLine("  <Size>620,500</Size>");
        sbFinal.AppendLine($"  <List>{encList}</List>");
        sbFinal.AppendLine("</Data>");

        return sbFinal.ToString();
    }
}
