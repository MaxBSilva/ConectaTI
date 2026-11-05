using System.Globalization;
using System.Text;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

public static class RdpSessionFile
{
    // MSTSC uses the file's base name for the connection caption. Randomness belongs to
    // the parent directory, so simultaneous launches never overwrite files or expose IDs.
    public static string CreatePath(string tempDirectory, ConnectionEntry entry)
    {
        var directory = Path.Combine(tempDirectory, $"conectati-{Guid.NewGuid():N}");
        var parsed = HostPortParser.Parse(entry.Host, ConnectionProtocol.Rdp);
        var host = parsed.IsValid ? parsed.CleanHost : entry.Host;
        var port = parsed.HasExplicitPort ? parsed.Port : entry.Port;
        var endpoint = port > 0 ? ConnectionValidator.FormatHostAndPort(host, port) : host;
        var name = string.IsNullOrWhiteSpace(entry.Name) ? L.Text("Conexão RDP") : entry.Name;
        // Keep the endpoint readable even for unusually long connection names. Leave room
        // for MSTSC's legacy path handling, as well as the extension and directory separators.
        var budget = Math.Min(180, 250 - Path.GetFullPath(directory).Length - 5);
        if (budget < 32) throw new PathTooLongException(L.Text("A pasta temporária RDP é longa demais para identificar a conexão."));
        var safeEndpoint = Sanitize(endpoint, Math.Min(100, budget - 16));
        var safeName = Sanitize(name, budget - safeEndpoint.Length - 3);
        return Path.Combine(directory, $"{safeName} - {safeEndpoint}.rdp");
    }

    private static string Sanitize(string value, int maxLength)
    {
        var text = new StringBuilder();
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            var part = rune.Value switch
            {
                ':' => "꞉", '/' or '\\' => "-", '"' => "’", '<' => "‹", '>' => "›",
                '|' => "¦", '?' => "？", '*' => "＊", _ => rune.ToString()
            };
            if (text.Length + part.Length > maxLength) break;
            text.Append(part);
        }
        var result = text.ToString().Trim().TrimEnd('.', ' ');
        return result.Length == 0 ? "RDP" : result;
    }
}
