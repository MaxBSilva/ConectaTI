using System.Globalization;
using System.Text;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
namespace ConectaTI.App.Services;

public record RdpImportResult(ConnectionEntry Entry, IReadOnlyList<string> Warnings);
public record RdpProperty(string Type, string Value);

public static class RdpFileService
{
    private record Transfer(RdpOptions Options, bool FullScreen, int Width, int Height, bool Multimon, bool Admin);
    public static string ExportMetadata(ConnectionEntry entry) => System.Text.Json.JsonSerializer.Serialize(
        new Transfer(entry.RdpOptions, entry.RdpFullScreen, entry.RdpWidth, entry.RdpHeight, entry.RdpUseMultimon, entry.RdpAdminSession));
    public static void ApplyMetadata(ConnectionEntry entry, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        var transfer = System.Text.Json.JsonSerializer.Deserialize<Transfer>(json) ?? throw new FormatException(L.Text("Metadados RDP inválidos."));
        entry.RdpOptions = transfer.Options ?? new();
        entry.RdpOptions.AdditionalProperties = Sanitize(entry.RdpOptions.AdditionalProperties);
        entry.RdpOptions.OriginalContent = SanitizeOriginal(entry.RdpOptions.OriginalContent);
        entry.RdpFullScreen = transfer.FullScreen; entry.RdpWidth = transfer.Width; entry.RdpHeight = transfer.Height;
        entry.RdpUseMultimon = transfer.Multimon; entry.RdpAdminSession = transfer.Admin;
    }
    private static readonly HashSet<string> Structured = new(StringComparer.OrdinalIgnoreCase)
    { "full address", "server port", "username", "domain", "screen mode id", "desktopwidth", "desktopheight", "use multimon", "administrative session", "gatewayhostname", "gatewayusagemethod", "gatewaycredentialssource", "gatewayprofileusagemethod", "promptcredentialonce" };
    private static bool Sensitive(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Equals("signature", StringComparison.OrdinalIgnoreCase) || key.Equals("signscope", StringComparison.OrdinalIgnoreCase);

    // Validate interpreted integer values; preserve documented gateway credential modes 0 through 5.
    private static readonly Dictionary<string, (int Min, int Max)> IntegerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["authentication level"] = (1, 3), ["enablecredsspsupport"] = (1, 1),
        ["negotiate security layer"] = (1, 1), ["enablerdsaadauth"] = (0, 1),
        ["prompt for credentials"] = (0, 1), ["prompt for credentials on client"] = (0, 1),
        ["promptcredentialonce"] = (0, 1), ["gatewayusagemethod"] = (0, 4),
        ["gatewaycredentialssource"] = (0, 5), ["gatewayprofileusagemethod"] = (0, 1)
    };
    private static void ValidateProperty(string key, string type, string value)
    {
        if (!IntegerDomains.TryGetValue(key, out var domain)) return;
        if (type != "i" || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number < domain.Min || number > domain.Max)
            throw new FormatException(L.Format("Propriedade RDP '{0}' inválida: use tipo i e inteiro entre {1} e {2}.", key, domain.Min, domain.Max));
    }

    public static Dictionary<string, RdpProperty> Parse(string text)
    {
        var properties = new Dictionary<string, RdpProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(':', 3);
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || parts[1] is not ("s" or "i" or "b")
                || parts.Any(p => p.Any(char.IsControl)))
                throw new FormatException(L.Text("Linha RDP inválida. Use propriedade:tipo:valor."));
            var key = parts[0].Trim();
            ValidateProperty(key, parts[1], parts[2]);
            if (Sensitive(key) || parts[1] == "b") continue;
            if (parts[1] == "i" && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new FormatException(L.Text("Propriedade numérica RDP inválida."));
            properties[key] = new(parts[1], parts[1] == "i"
                ? int.Parse(parts[2], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : parts[2]);
        }
        return properties;
    }
    public static string Sanitize(string text) => Serialize(Parse(text));
    public static string SanitizeOriginal(string text)
    {
        Parse(text);
        return string.Join("\n", text.TrimStart('\uFEFF').Split('\n').Where(line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return true;
            var parts = line.TrimEnd('\r').Split(':', 3);
            return !Sensitive(parts[0].Trim()) && parts[1] != "b";
        }));
    }
    public static string Serialize(IReadOnlyDictionary<string, RdpProperty> values) =>
        string.Join("\r\n", values.Select(p => $"{p.Key}:{p.Value.Type}:{p.Value.Value}")) + "\r\n";

    public static RdpImportResult ImportFile(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new FormatException(L.Text("Arquivo RDP excede 4 MB."));
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return Import(reader.ReadToEnd(), Path.GetFileNameWithoutExtension(path));
    }
    public static RdpImportResult Import(string text, string name)
    {
        var p = Parse(text);
        var warnings = new List<string>();
        string S(string key, string fallback = "") => p.TryGetValue(key, out var v) && v.Type == "s" ? v.Value : fallback;
        int I(string key, int fallback) => p.TryGetValue(key, out var v) && v.Type == "i" ? int.Parse(v.Value, CultureInfo.InvariantCulture) : fallback;
        var address = S("full address").Trim();
        var admin = I("administrative session", 0) == 1;
        if (address.EndsWith(" /admin", StringComparison.OrdinalIgnoreCase))
        {
            address = address[..^7].TrimEnd(); admin = true;
            warnings.Add(L.Text("Normalização: /admin foi separado do hostname e habilitado como opção administrativa."));
        }
        var host = HostPortParser.Parse(address, ConnectionProtocol.Rdp);
        if (!host.IsValid || !ConnectionValidator.IsValidHost(host.CleanHost, out _, out _))
            throw new FormatException(L.Text("Endereço RDP inválido. Somente o sufixo /admin é reconhecido na importação."));
        var entry = new ConnectionEntry
        {
            Name = name, Host = host.CleanHost, Port = host.HasExplicitPort ? host.Port : I("server port", 3389),
            Username = S("username"), Domain = S("domain"), RdpAdminSession = admin,
            RdpFullScreen = I("screen mode id", 2) == 2, RdpUseMultimon = I("use multimon", 0) == 1,
            RdpWidth = I("desktopwidth", 1920), RdpHeight = I("desktopheight", 1080),
            RdpOptions = new()
            {
                GatewayHostname = S("gatewayhostname"), GatewayUsageMethod = I("gatewayusagemethod", 2),
                GatewayCredentialsSource = I("gatewaycredentialssource", 4), GatewayProfileUsageMethod = I("gatewayprofileusagemethod", 1),
                PromptCredentialOnce = I("promptcredentialonce", 1) == 1,
                OriginalContent = SanitizeOriginal(text),
                AdditionalProperties = Serialize(p.Where(k => !Structured.Contains(k.Key)).ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase))
            }
        };
        if (text.Split('\n').Any(l => l.TrimStart().StartsWith("signature:", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(L.Text("O arquivo contém assinatura. A cópia e a saída gerada não são assinadas; alterações invalidam a assinatura original."));
        warnings.Add(L.Text("Senhas, dados binários e assinatura são omitidos da cópia de comparação e da importação. Revise os dados e use Salvar para criar um novo acesso."));
        Build(entry);
        return new(entry, warnings);
    }
    public static string Build(ConnectionEntry entry, NormalizedUserIdentity? identity = null, bool hasPassword = false)
    {
        var p = Parse(entry.RdpOptions.AdditionalProperties);
        foreach (var key in Structured) p.Remove(key);
        void Set(string k, string t, object v) => p[k] = new(t, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
        void Default(string k, int v) { if (!p.ContainsKey(k)) Set(k, "i", v); }
        Set("full address", "s", ConnectionValidator.FormatRdpFullAddress(entry.Host, entry.Port));
        Set("server port", "i", entry.Port);
        Set("screen mode id", "i", entry.RdpFullScreen ? 2 : 1);
        Set("desktopwidth", "i", entry.RdpWidth); Set("desktopheight", "i", entry.RdpHeight);
        Set("use multimon", "i", entry.RdpUseMultimon ? 1 : 0);
        var user = identity ?? UserIdentityFormatter.Normalize(entry.Username, entry.Domain);
        if (!string.IsNullOrWhiteSpace(user.EffectiveDownLevelLogonName))
        {
            Set("username", "s", user.IsUpn ? user.RawUsername : user.CleanUsername);
            if (!user.IsUpn && user.Domain.Length > 0) Set("domain", "s", user.Domain);
        }
        Set("administrative session", "i", entry.RdpAdminSession ? 1 : 0);
        var g = entry.RdpOptions;
        ValidateProperty("gatewayusagemethod", "i", g.GatewayUsageMethod.ToString(CultureInfo.InvariantCulture));
        ValidateProperty("gatewaycredentialssource", "i", g.GatewayCredentialsSource.ToString(CultureInfo.InvariantCulture));
        ValidateProperty("gatewayprofileusagemethod", "i", g.GatewayProfileUsageMethod.ToString(CultureInfo.InvariantCulture));
        if (g.GatewayHostname.Length > 0)
        {
            var gateway = HostPortParser.Parse(g.GatewayHostname, ConnectionProtocol.Rdp);
            if (!gateway.IsValid || !ConnectionValidator.IsValidHost(gateway.CleanHost, out _, out _)) throw new FormatException(L.Text("Servidor de gateway inválido."));
            Set("gatewayhostname", "s", g.GatewayHostname);
            Set("gatewayusagemethod", "i", g.GatewayUsageMethod);
            Set("gatewaycredentialssource", "i", g.GatewayCredentialsSource);
            Set("gatewayprofileusagemethod", "i", g.GatewayProfileUsageMethod);
            Set("promptcredentialonce", "i", g.PromptCredentialOnce ? 1 : 0);
        }
        Default("authentication level", 2); Default("enablecredsspsupport", 1);
        Default("prompt for credentials", hasPassword ? 0 : 1);
        Default("prompt for credentials on client", hasPassword ? 0 : 1);
        foreach (var property in p) ValidateProperty(property.Key, property.Value.Type, property.Value.Value);
        var result = Serialize(p);
        Parse(result); // Reject line injection from structured fields too.
        return result;
    }
}



