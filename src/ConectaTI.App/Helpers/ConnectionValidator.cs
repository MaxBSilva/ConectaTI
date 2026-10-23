using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ConectaTI.App.Models;

namespace ConectaTI.App.Helpers;

public static partial class ConnectionValidator
{
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-_]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-_]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.Compiled)]
    private static partial Regex DnsHostnameRegex();

    public static bool IsValidPort(int port) => port >= 1 && port <= 65535;

    public static bool ContainsLineBreakOrControlChars(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        foreach (var ch in input)
        {
            if (ch is '\r' or '\n' or '\0' || char.IsControl(ch))
                return true;
        }
        return false;
    }

    public static string SanitizeSingleLineForRdp(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        return input
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    public static bool IsValidHost(string? hostInput, out string normalizedHost, out bool isIpv6)
    {
        normalizedHost = string.Empty;
        isIpv6 = false;

        if (string.IsNullOrWhiteSpace(hostInput))
            return false;

        if (ContainsLineBreakOrControlChars(hostInput))
            return false;

        var candidate = hostInput.Trim();

        // Remove colchetes externos se for IPv6 como [2001:db8::1]
        if (candidate.StartsWith('[') && candidate.EndsWith(']') && candidate.Length > 2)
        {
            candidate = candidate[1..^1];
        }

        // Verifica IPv4 ou IPv6
        if (IPAddress.TryParse(candidate, out var ipAddress))
        {
            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                normalizedHost = candidate;
                isIpv6 = true;
                return true;
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                if (candidate.Contains('.'))
                {
                    normalizedHost = ipAddress.ToString();
                    isIpv6 = false;
                    return true;
                }
            }
        }

        // Verifica DNS / Hostname
        if (candidate.Length <= 253 && DnsHostnameRegex().IsMatch(candidate))
        {
            normalizedHost = candidate;
            isIpv6 = false;
            return true;
        }

        return false;
    }

    public static string FormatHostAndPort(string host, int port)
    {
        var clean = SanitizeSingleLineForRdp(host);
        if (clean.StartsWith('[') && clean.EndsWith(']'))
        {
            clean = clean[1..^1];
        }

        if (IPAddress.TryParse(clean, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{clean}]:{port}";
        }

        return $"{clean}:{port}";
    }

    public static string FormatRdpFullAddress(string host, int port)
    {
        var clean = SanitizeSingleLineForRdp(host);
        if (clean.StartsWith('[') && clean.EndsWith(']'))
        {
            clean = clean[1..^1];
        }

        var isIpv6 = IPAddress.TryParse(clean, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
        var hostPart = isIpv6 ? $"[{clean}]" : clean;

        return port == 3389 ? hostPart : $"{hostPart}:{port}";
    }

    public static string StripIpv6Brackets(string host)
    {
        var clean = SanitizeSingleLineForRdp(host);
        if (clean.StartsWith('[') && clean.EndsWith(']') && clean.Length > 2)
        {
            return clean[1..^1];
        }
        return clean;
    }

    public static List<string> ValidateConnection(ConnectionEntry entry)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Name))
            errors.Add("O nome da conexão é obrigatório.");
        else if (ContainsLineBreakOrControlChars(entry.Name))
            errors.Add("O nome da conexão não pode conter quebras de linha.");

        if (string.IsNullOrWhiteSpace(entry.ClientName))
            entry.ClientName = "Geral";
        else if (ContainsLineBreakOrControlChars(entry.ClientName))
            errors.Add("O cliente não pode conter quebras de linha.");

        if (!IsValidHost(entry.Host, out _, out _))
            errors.Add("O endereço / hostname (DNS, IPv4 ou IPv6) é inválido ou está vazio.");

        if (!IsValidPort(entry.Port))
            errors.Add("A porta deve estar entre 1 e 65535.");

        if (ContainsLineBreakOrControlChars(entry.Username))
            errors.Add("O usuário não pode conter quebras de linha ou caracteres de controle.");

        if (ContainsLineBreakOrControlChars(entry.Domain))
            errors.Add("O domínio não pode conter quebras de linha ou caracteres de controle.");

        if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPath))
        {
            var keyPath = entry.PrivateKeyPath.Trim();
            if (ContainsLineBreakOrControlChars(keyPath) || keyPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add("O caminho da chave privada contém caracteres inválidos.");
            }
        }

        if (entry.Protocol == ConnectionProtocol.Rdp && !entry.RdpFullScreen)
        {
            if (entry.RdpWidth < 640 || entry.RdpWidth > 7680 || entry.RdpHeight < 480 || entry.RdpHeight > 4320)
            {
                errors.Add("A resolução da Área de Trabalho Remota deve estar entre 640x480 e 7680x4320.");
            }
        }

        return errors;
    }
}
