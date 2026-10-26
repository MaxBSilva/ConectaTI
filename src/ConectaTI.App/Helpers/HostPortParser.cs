using System.Globalization;
using System.Net;
using ConectaTI.App.Models;

namespace ConectaTI.App.Helpers;

public readonly record struct HostPortParseResult(
    string CleanHost,
    int Port,
    bool HasExplicitPort,
    bool IsValid,
    string? ErrorMessage);

/// <summary>
/// Trata e valida a combinação e separação de Endereço/Hostname e Porta,
/// com suporte completo a IPv4, IPv6 (com e sem colchetes) e validação de porta (1-65535).
/// </summary>
public static class HostPortParser
{
    public static string FormatDisplay(string? host, int port, ConnectionProtocol protocol)
    {
        var cleanHost = (host ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanHost))
            return string.Empty;

        // Se o host já contém porta interna (ex: inserido como "10.0.0.1:3390" ou "[::1]:3390"), separa primeiro para não duplicar sufixo
        var parsed = Parse(cleanHost, protocol);
        cleanHost = parsed.CleanHost;
        var defaultPort = protocol.GetDefaultPort();
        var effectivePort = parsed.HasExplicitPort ? parsed.Port : (port > 0 ? port : defaultPort);

        var isCustomPort = effectivePort > 0 && effectivePort != defaultPort;

        if (!isCustomPort)
        {
            // Porta padrão: retorna apenas o host
            return cleanHost;
        }

        // Porta personalizada: se for IPv6, formata com colchetes
        if (cleanHost.Contains(':') && !cleanHost.StartsWith('['))
        {
            return $"[{cleanHost}]:{effectivePort}";
        }

        return $"{cleanHost}:{effectivePort}";
    }

    public static HostPortParseResult Parse(string? input, ConnectionProtocol protocol)
    {
        var raw = (input ?? string.Empty).Trim();
        var defaultPort = protocol.GetDefaultPort();

        if (string.IsNullOrEmpty(raw))
        {
            return new HostPortParseResult(string.Empty, defaultPort, false, false, L.Text("O endereço/hostname não pode ficar em branco."));
        }

        // Caso 1: Notação IPv6 com colchetes: [2001:db8::1]:port ou [2001:db8::1]
        if (raw.StartsWith('['))
        {
            var closingBracket = raw.IndexOf(']');
            if (closingBracket < 0)
            {
                return new HostPortParseResult(raw, defaultPort, false, false, L.Text("Endereço IPv6 com colchete de fechamento ausente."));
            }

            var ipv6Part = raw[1..closingBracket].Trim();
            var remainder = raw[(closingBracket + 1)..].Trim();

            if (string.IsNullOrEmpty(remainder))
            {
                return new HostPortParseResult(ipv6Part, defaultPort, false, true, null);
            }

            if (remainder.StartsWith(':'))
            {
                var portStr = remainder[1..].Trim();
                if (int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customPort) &&
                    customPort is >= 1 and <= 65535)
                {
                    return new HostPortParseResult(ipv6Part, customPort, true, true, null);
                }

                return new HostPortParseResult(ipv6Part, defaultPort, true, false, L.Format("Porta inválida '{0}'. A porta deve estar entre 1 e 65535.", (portStr)));
            }

            return new HostPortParseResult(ipv6Part, defaultPort, false, false, L.Format("Formato de endereço inválido após o colchete: '{0}'.", (remainder)));
        }

        // Caso 2: IPv6 literal sem colchetes (ex: 2001:db8::1)
        // Se houver mais de um ':' e não estiver entre colchetes, é um IPv6 puro sem porta
        var colonCount = raw.Count(c => c == ':');
        if (colonCount > 1)
        {
            if (IPAddress.TryParse(raw, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return new HostPortParseResult(raw, defaultPort, false, true, null);
            }

            // Caso contenha múltiplos dois-pontos mas termine em :porta após colchetes
            return new HostPortParseResult(raw, defaultPort, false, false, L.Text("Para especificar porta em endereços IPv6, utilize a notação [endereço]:porta."));
        }

        // Caso 3: Hostname ou IPv4 com porta única (ex: 10.30.0.11:3389 ou servidor:3390)
        if (colonCount == 1)
        {
            var parts = raw.Split(':');
            var hostPart = parts[0].Trim();
            var portPart = parts[1].Trim();

            if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customPort) &&
                customPort is >= 1 and <= 65535)
            {
                return new HostPortParseResult(hostPart, customPort, true, true, null);
            }

            return new HostPortParseResult(hostPart, defaultPort, true, false, L.Format("Porta inválida '{0}'. A porta deve estar entre 1 e 65535.", (portPart)));
        }

        // Caso 4: Hostname ou IPv4 padrão sem porta (ex: 10.30.0.11 ou servidor.cliente.com.br)
        return new HostPortParseResult(raw, defaultPort, false, true, null);
    }
}
