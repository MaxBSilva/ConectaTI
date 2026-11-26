using System.Diagnostics;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

/// <summary>
/// Valida, normaliza e abre até 3 sites HTTP/HTTPS no navegador padrão do Windows
/// quando o aplicativo é iniciado automaticamente junto com a sessão do Windows.
/// </summary>
public static class StartupWebsitesService
{
    public const string WindowsStartupArg = "--windows-startup";

    public static bool IsWindowsStartupInvocation(string[]? args)
        => args is not null && Array.Exists(args, a => string.Equals(a, WindowsStartupArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normaliza uma URL informada pelo usuário, aceitando exclusivamente esquemas http:// ou https://.
    /// Caso nenhum esquema seja informado (ex.: "painel.empresa.com.br"), prefixa automaticamente "https://".
    /// Rejeita caminhos locais, executáveis ou esquemas não web.
    /// </summary>
    public static bool TryNormalizeHttpUrl(string? rawInput, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        var trimmed = rawInput.Trim();
        if (trimmed.Length > 2048 || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return false;

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("/", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            return false;
        }

        string candidate;
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed;
        }
        else if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }
        else
        {
            candidate = "https://" + trimmed;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }

        if (uri.PathAndQuery == "/" && string.IsNullOrEmpty(uri.Fragment) && !trimmed.EndsWith('/'))
        {
            normalizedUrl = $"{uri.Scheme}://{uri.Authority}";
        }
        else
        {
            normalizedUrl = uri.AbsoluteUri;
        }

        return true;
    }

    public static bool ValidateSettingsInputs(
        string? site1,
        string? site2,
        string? site3,
        out string normalized1,
        out string normalized2,
        out string normalized3,
        out int invalidSlotOneBased)
    {
        normalized1 = string.Empty;
        normalized2 = string.Empty;
        normalized3 = string.Empty;
        invalidSlotOneBased = 0;

        if (!string.IsNullOrWhiteSpace(site1))
        {
            if (!TryNormalizeHttpUrl(site1, out normalized1))
            {
                invalidSlotOneBased = 1;
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(site2))
        {
            if (!TryNormalizeHttpUrl(site2, out normalized2))
            {
                invalidSlotOneBased = 2;
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(site3))
        {
            if (!TryNormalizeHttpUrl(site3, out normalized3))
            {
                invalidSlotOneBased = 3;
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> GetConfiguredUrls(AppSettings? settings)
    {
        if (settings is null)
            return Array.Empty<string>();

        var result = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in new[] { settings.StartupWebsite1, settings.StartupWebsite2, settings.StartupWebsite3 })
        {
            if (TryNormalizeHttpUrl(raw, out var normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public static int LaunchConfiguredWebsites(
        AppSettings? settings,
        AppLogger? logger = null,
        Action<string>? browserLauncherOverride = null)
    {
        var urls = GetConfiguredUrls(settings);
        if (urls.Count == 0)
            return 0;

        var opened = 0;
        foreach (var url in urls)
        {
            try
            {
                if (browserLauncherOverride is not null)
                {
                    browserLauncherOverride(url);
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                opened++;
            }
            catch (Exception ex)
            {
                var safeHost = Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "site configurado";
                logger?.Warn("StartupWebsites", $"Não foi possível abrir '{safeHost}' no navegador padrão: {ex.Message}");
            }
        }

        if (opened > 0)
        {
            logger?.Info("StartupWebsites", $"{opened} site(s) inicial(is) enviado(s) ao navegador padrão.");
        }

        return opened;
    }
}
