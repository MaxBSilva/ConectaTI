using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ConectaTI.App.Models;
using Microsoft.Win32;

namespace ConectaTI.App.Services.Launchers;

public enum VpnUnreachableDecision
{
    Cancel = 0,
    OpenVpn = 1,
    ConnectAnyway = 2
}

public sealed record VpnLaunchResolution(
    bool Found,
    string DisplayClientName,
    string TargetPathOrUri,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute,
    bool IsWindowsNativeFallback,
    string? HintMessage = null);

public sealed record VpnLaunchOutcome(
    bool Started,
    bool UsedWindowsFallback,
    string ClientDisplayName,
    string StatusMessage,
    string? ErrorMessage = null);

/// <summary>
/// Localiza e inicia clientes de VPN heterogêneos (FortiClient, OpenConnect GUI, OpenVPN GUI/Connect,
/// Cisco AnyConnect/Secure Client, SonicWall NetExtender, GlobalProtect, WireGuard, Sophos, WatchGuard,
/// VPN nativa do Windows ou executáveis personalizados), tolerando variações de grafia nos cadastros.
/// </summary>
public sealed class VpnClientLauncherService
{
    public const string WindowsVpnSettingsUri = "ms-settings:network-vpn";

    public static IReadOnlyList<string> CommonVpnClientPresets { get; } =
    [
        "FortiClient",
        "OpenConnect GUI",
        "OpenVPN GUI",
        "OpenVPN Connect",
        "Cisco Secure Client (AnyConnect)",
        "SonicWall NetExtender",
        "Palo Alto GlobalProtect",
        "Sophos Connect",
        "WatchGuard Mobile VPN",
        "WireGuard",
        "Check Point Endpoint",
        "Pritunl",
        "VPN do Windows"
    ];

    private sealed record KnownVpnVendor(
        string DisplayName,
        string[] Keywords,
        string[] ProcessNames,
        string[] RelativeCandidatePaths,
        string[] StartMenuPatterns);

    private static readonly KnownVpnVendor[] KnownVendors =
    [
        new(
            DisplayName: "FortiClient",
            Keywords: ["forticlient", "fortcliente", "fortclient", "fortinet", "fortivpn", "fortitray", "forti"],
            ProcessNames: ["FortiClient", "FortiClientConsole", "FortiTray"],
            RelativeCandidatePaths:
            [
                @"Fortinet\FortiClient\FortiClient.exe",
                @"Fortinet\FortiClient\FortiClientConsole.exe"
            ],
            StartMenuPatterns: ["FortiClient"]),

        new(
            DisplayName: "OpenConnect GUI",
            Keywords: ["openconnect", "openconect", "openconnectgui"],
            ProcessNames: ["openconnect-gui"],
            RelativeCandidatePaths:
            [
                @"OpenConnect-GUI\openconnect-gui.exe",
                @"OpenConnect\openconnect-gui.exe"
            ],
            StartMenuPatterns: ["OpenConnect"]),

        new(
            DisplayName: "OpenVPN Connect",
            Keywords: ["openvpnconnect"],
            ProcessNames: ["OpenVPNConnect"],
            RelativeCandidatePaths:
            [
                @"OpenVPN Connect\OpenVPNConnect.exe",
                @"OpenVPN Technologies\OpenVPN Connect\OpenVPNConnect.exe"
            ],
            StartMenuPatterns: ["OpenVPN Connect"]),

        new(
            DisplayName: "OpenVPN GUI",
            Keywords: ["openvpngui", "opengui", "openvpn", "ovpn"],
            ProcessNames: ["openvpn-gui", "OpenVPNConnect"],
            RelativeCandidatePaths:
            [
                @"OpenVPN\bin\openvpn-gui.exe",
                @"OpenVPN Connect\OpenVPNConnect.exe",
                @"OpenConnect-GUI\openconnect-gui.exe"
            ],
            StartMenuPatterns: ["OpenVPN", "OpenConnect"]),

        new(
            DisplayName: "Cisco Secure Client / AnyConnect",
            Keywords: ["anyconnect", "ciscosecure", "ciscovpn", "vpnui", "cscui", "cisco"],
            ProcessNames: ["csc_ui", "vpnui"],
            RelativeCandidatePaths:
            [
                @"Cisco\Cisco Secure Client\UI\csc_ui.exe",
                @"Cisco\Cisco AnyConnect Secure Mobility Client\vpnui.exe"
            ],
            StartMenuPatterns: ["Cisco Secure Client", "AnyConnect"]),

        new(
            DisplayName: "SonicWall NetExtender",
            Keywords: ["netextender", "sonicwall", "negui", "mobileconnect"],
            ProcessNames: ["NEGui", "NetExtender"],
            RelativeCandidatePaths:
            [
                @"SonicWall\SSL-VPN\NetExtender\NEGui.exe",
                @"SonicWALL\SSL-VPN\NetExtender\NEGui.exe"
            ],
            StartMenuPatterns: ["NetExtender", "SonicWall"]),

        new(
            DisplayName: "Palo Alto GlobalProtect",
            Keywords: ["globalprotect", "paloalto", "pangpa"],
            ProcessNames: ["PanGPA"],
            RelativeCandidatePaths:
            [
                @"Palo Alto Networks\GlobalProtect\PanGPA.exe"
            ],
            StartMenuPatterns: ["GlobalProtect"]),

        new(
            DisplayName: "Sophos Connect",
            Keywords: ["sophos", "scgui"],
            ProcessNames: ["scgui"],
            RelativeCandidatePaths:
            [
                @"Sophos\Connect\GUI\scgui.exe",
                @"Sophos\Sophos SSL VPN Client\bin\openvpn-gui.exe"
            ],
            StartMenuPatterns: ["Sophos Connect", "Sophos SSL VPN"]),

        new(
            DisplayName: "WatchGuard Mobile VPN",
            Keywords: ["watchguard", "wgsslvpnc"],
            ProcessNames: ["wgsslvpnc"],
            RelativeCandidatePaths:
            [
                @"WatchGuard\WatchGuard Mobile VPN with SSL\wgsslvpnc.exe"
            ],
            StartMenuPatterns: ["WatchGuard"]),

        new(
            DisplayName: "WireGuard",
            Keywords: ["wireguard"],
            ProcessNames: ["wireguard"],
            RelativeCandidatePaths:
            [
                @"WireGuard\wireguard.exe"
            ],
            StartMenuPatterns: ["WireGuard"]),

        new(
            DisplayName: "Check Point Endpoint VPN",
            Keywords: ["checkpoint", "capsulevpn", "trgui", "endpointconnect"],
            ProcessNames: ["TrGUI"],
            RelativeCandidatePaths:
            [
                @"CheckPoint\Endpoint Connect\TrGUI.exe",
                @"CheckPoint\Endpoint Security\Endpoint Connect\TrGUI.exe"
            ],
            StartMenuPatterns: ["Endpoint Connect", "Check Point"]),

        new(
            DisplayName: "Pritunl",
            Keywords: ["pritunl"],
            ProcessNames: ["pritunl", "Pritunl"],
            RelativeCandidatePaths:
            [
                @"Pritunl\pritunl.exe"
            ],
            StartMenuPatterns: ["Pritunl"]),

        new(
            DisplayName: "SoftEther VPN",
            Keywords: ["softether", "vpncmgr"],
            ProcessNames: ["vpncmgr", "vpncmgr_x64"],
            RelativeCandidatePaths:
            [
                @"SoftEther VPN Client\vpncmgr_x64.exe",
                @"SoftEther VPN Client\vpncmgr.exe"
            ],
            StartMenuPatterns: ["SoftEther"]),

        new(
            DisplayName: "Tailscale / ZeroTier / Radmin VPN",
            Keywords: ["tailscale", "zerotier", "radminvpn", "radmin", "hamachi"],
            ProcessNames: ["tailscale-ipn", "zerotier_desktop_ui", "RvControlSvc", "hamachi-2-ui"],
            RelativeCandidatePaths:
            [
                @"Tailscale\tailscale-ipn.exe",
                @"ZeroTier\One\zerotier_desktop_ui.exe",
                @"Famatech\Radmin VPN\RadminVPN.exe",
                @"LogMeIn Hamachi\hamachi-2-ui.exe"
            ],
            StartMenuPatterns: ["Tailscale", "ZeroTier", "Radmin VPN", "Hamachi"])
    ];

    private static readonly string[] WindowsNativeKeywords =
    [
        "vpnwindows", "vpndowindows", "windows", "nativa", "rasphone", "discador", "l2tp", "pptp", "sstp", "ikev2"
    ];

    private readonly AppLogger? _logger;
    private readonly Func<ProcessStartInfo, bool>? _customStarter;
    private readonly IReadOnlyList<string>? _customProgramRoots;
    private readonly IReadOnlyList<string>? _customStartMenuRoots;

    public VpnClientLauncherService(
        AppLogger? logger = null,
        Func<ProcessStartInfo, bool>? customStarter = null,
        IReadOnlyList<string>? customProgramRoots = null,
        IReadOnlyList<string>? customStartMenuRoots = null)
    {
        _logger = logger;
        _customStarter = customStarter;
        _customProgramRoots = customProgramRoots;
        _customStartMenuRoots = customStartMenuRoots;
    }

    public static string CompactNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public VpnLaunchResolution Resolve(ConnectionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var rawApp = (entry.VpnApp ?? string.Empty).Trim();
        var rawName = (entry.VpnName ?? string.Empty).Trim();
        var rawNotes = (entry.VpnNotes ?? string.Empty).Trim();

        // 1. Caminho explícito de executável/atalho informado em VpnApp
        if (!string.IsNullOrWhiteSpace(rawApp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawApp.Trim('"'));
            if (File.Exists(expanded))
            {
                var useShell = !expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var display = Path.GetFileNameWithoutExtension(expanded);
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: string.IsNullOrWhiteSpace(display) ? rawApp : display,
                    TargetPathOrUri: expanded,
                    Arguments: BuildOptionalProfileArguments(expanded, rawName),
                    UseShellExecute: useShell,
                    IsWindowsNativeFallback: false);
            }
        }

        // 2. Verifica se é explicitamente VPN nativa do Windows
        var normApp = CompactNormalize(rawApp);
        var normCombined = CompactNormalize($"{rawApp} {rawName} {rawNotes}");

        if (!string.IsNullOrEmpty(normApp) && WindowsNativeKeywords.Any(normApp.Contains))
        {
            return BuildWindowsNativeResolution(rawName);
        }

        // 3. Identificação tolerante a variações ("fortcliente", "open connect", "open gui", etc.)
        // Prioriza VpnApp; se vazio ou genérico, inspeciona também VpnName e VpnNotes.
        var matchedVendor = FindVendorByText(normApp) ?? FindVendorByText(normCombined);
        if (matchedVendor is not null)
        {
            var located = TryLocateVendorExecutableOrShortcut(matchedVendor);
            if (!string.IsNullOrWhiteSpace(located))
            {
                var useShell = !located.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var args = BuildOptionalProfileArguments(located, rawName);
                var hint = BuildRunningTrayHintIfApplicable(located, args);
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: matchedVendor.DisplayName,
                    TargetPathOrUri: located,
                    Arguments: args,
                    UseShellExecute: useShell,
                    IsWindowsNativeFallback: false,
                    HintMessage: hint);
            }

            return new VpnLaunchResolution(
                Found: false,
                DisplayClientName: matchedVendor.DisplayName,
                TargetPathOrUri: string.Empty,
                Arguments: Array.Empty<string>(),
                UseShellExecute: false,
                IsWindowsNativeFallback: false);
        }

        // 4. Busca genérica no Menu Iniciar / App Paths / PATH pelo texto digitado em VpnApp
        if (!string.IsNullOrWhiteSpace(rawApp))
        {
            var genericMatch = TrySearchGenericApp(rawApp);
            if (!string.IsNullOrWhiteSpace(genericMatch))
            {
                var useShell = !genericMatch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: rawApp,
                    TargetPathOrUri: genericMatch,
                    Arguments: Array.Empty<string>(),
                    UseShellExecute: useShell,
                    IsWindowsNativeFallback: false);
            }

            return new VpnLaunchResolution(
                Found: false,
                DisplayClientName: rawApp,
                TargetPathOrUri: string.Empty,
                Arguments: Array.Empty<string>(),
                UseShellExecute: false,
                IsWindowsNativeFallback: false);
        }

        // 5. Se VpnApp estiver em branco (apenas Nome/Servidor VPN preenchido), abre VPN nativa do Windows
        return BuildWindowsNativeResolution(rawName);
    }

    public VpnLaunchOutcome Launch(ConnectionEntry entry)
    {
        var resolution = Resolve(entry);
        if (!resolution.Found)
        {
            return new VpnLaunchOutcome(
                Started: false,
                UsedWindowsFallback: false,
                ClientDisplayName: resolution.DisplayClientName,
                StatusMessage: L.Format("Cliente VPN '{0}' não localizado automaticamente.", resolution.DisplayClientName),
                ErrorMessage: L.Format(
                    "Não foi possível localizar o cliente VPN '{0}' neste computador.\n\n" +
                    "Dica: no bloco 'VPN do cliente', use o botão '...' ao lado de Aplicativo para indicar o executável ou abra as Configurações de VPN do Windows.",
                    resolution.DisplayClientName));
        }

        return ExecuteResolution(resolution);
    }

    public VpnLaunchOutcome LaunchWindowsVpnSettingsFallback()
    {
        var fallback = new VpnLaunchResolution(
            Found: true,
            DisplayClientName: L.Text("VPN do Windows"),
            TargetPathOrUri: WindowsVpnSettingsUri,
            Arguments: Array.Empty<string>(),
            UseShellExecute: true,
            IsWindowsNativeFallback: true);
        return ExecuteResolution(fallback);
    }

    private VpnLaunchOutcome ExecuteResolution(VpnLaunchResolution resolution)
    {
        try
        {
            // Se o processo já estiver aberto com janela principal visível/minimizada, traz para frente também
            if (_customStarter is null && resolution.TargetPathOrUri.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                TryFocusExistingProcessWindow(Path.GetFileNameWithoutExtension(resolution.TargetPathOrUri));
            }

            var psi = new ProcessStartInfo
            {
                FileName = resolution.TargetPathOrUri,
                UseShellExecute = resolution.UseShellExecute
            };

            if (!resolution.UseShellExecute)
            {
                foreach (var arg in resolution.Arguments)
                    psi.ArgumentList.Add(arg);
                var dir = Path.GetDirectoryName(resolution.TargetPathOrUri);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    psi.WorkingDirectory = dir;
            }

            if (_customStarter is not null)
            {
                var ok = _customStarter(psi);
                if (!ok)
                {
                    return new VpnLaunchOutcome(
                        Started: false,
                        UsedWindowsFallback: resolution.IsWindowsNativeFallback,
                        ClientDisplayName: resolution.DisplayClientName,
                        StatusMessage: L.Format("Não foi possível iniciar {0}.", resolution.DisplayClientName),
                        ErrorMessage: L.Format("Falha ao iniciar '{0}'.", resolution.DisplayClientName));
                }
            }
            else
            {
                Process.Start(psi);
            }

            var status = !string.IsNullOrWhiteSpace(resolution.HintMessage)
                ? resolution.HintMessage!
                : L.Format("Cliente VPN aberto: {0}", resolution.DisplayClientName);

            _logger?.Info("VPN", $"Cliente VPN acionado: {resolution.DisplayClientName}");
            return new VpnLaunchOutcome(
                Started: true,
                UsedWindowsFallback: resolution.IsWindowsNativeFallback,
                ClientDisplayName: resolution.DisplayClientName,
                StatusMessage: status);
        }
        catch (Exception ex)
        {
            _logger?.Warn("VPN", $"Erro ao iniciar cliente VPN '{resolution.DisplayClientName}': {ex.Message}");
            return new VpnLaunchOutcome(
                Started: false,
                UsedWindowsFallback: resolution.IsWindowsNativeFallback,
                ClientDisplayName: resolution.DisplayClientName,
                StatusMessage: L.Format("Erro ao abrir {0}.", resolution.DisplayClientName),
                ErrorMessage: ex.Message);
        }
    }

    private static VpnLaunchResolution BuildWindowsNativeResolution(string rawVpnName)
    {
        var sysDir = Environment.SystemDirectory;
        var rasphone = !string.IsNullOrWhiteSpace(sysDir) ? Path.Combine(sysDir, "rasphone.exe") : string.Empty;
        if (!string.IsNullOrWhiteSpace(rawVpnName) && !string.IsNullOrWhiteSpace(rasphone) && File.Exists(rasphone))
        {
            // Verifica se o nome existe em algum pbk padrão antes de passar -d, caso contrário abre configurações de VPN do Windows
            if (RasPhonebookContainsEntry(rawVpnName))
            {
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"VPN do Windows ({rawVpnName})",
                    TargetPathOrUri: rasphone,
                    Arguments: ["-d", rawVpnName],
                    UseShellExecute: false,
                    IsWindowsNativeFallback: true);
            }
        }

        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: L.Text("VPN do Windows"),
            TargetPathOrUri: WindowsVpnSettingsUri,
            Arguments: Array.Empty<string>(),
            UseShellExecute: true,
            IsWindowsNativeFallback: true);
    }

    private static bool RasPhonebookContainsEntry(string entryName)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var pbkPaths = new[]
            {
                Path.Combine(appData, @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
                Path.Combine(commonAppData, @"Microsoft\Network\Connections\Pbk\rasphone.pbk")
            };

            var bracketHeader = $"[{entryName.Trim()}]";
            foreach (var pbk in pbkPaths)
            {
                if (File.Exists(pbk))
                {
                    foreach (var line in File.ReadLines(pbk))
                    {
                        if (string.Equals(line.Trim(), bracketHeader, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static KnownVpnVendor? FindVendorByText(string compactNormalizedText)
    {
        if (string.IsNullOrEmpty(compactNormalizedText))
            return null;

        foreach (var vendor in KnownVendors)
        {
            foreach (var kw in vendor.Keywords)
            {
                if (compactNormalizedText.Contains(kw, StringComparison.Ordinal))
                    return vendor;
            }
        }

        return null;
    }

    private string? TryLocateVendorExecutableOrShortcut(KnownVpnVendor vendor)
    {
        // 1. Se já estiver em execução (apenas em ambiente real sem roots customizados de teste), obtém o caminho real do processo
        if (_customProgramRoots is null)
        {
            foreach (var procName in vendor.ProcessNames)
            {
                var runningPath = TryGetRunningProcessPath(procName);
                if (!string.IsNullOrWhiteSpace(runningPath) && File.Exists(runningPath))
                    return runningPath;
            }
        }

        // 2. Caminhos padrão nos diretórios de programas
        foreach (var root in GetProgramRoots())
        {
            foreach (var rel in vendor.RelativeCandidatePaths)
            {
                var normalizedRel = rel.Replace('\\', Path.DirectorySeparatorChar);
                var candidate = Path.Combine(root, normalizedRel);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // 3. App Paths no Registro e PATH do sistema (quando em execução normal)
        if (_customProgramRoots is null)
        {
            foreach (var rel in vendor.RelativeCandidatePaths)
            {
                var exeName = Path.GetFileName(rel.Replace('\\', Path.DirectorySeparatorChar));
                var fromRegOrPath = TryFindInAppPathsOrSystemPath(exeName);
                if (!string.IsNullOrWhiteSpace(fromRegOrPath))
                    return fromRegOrPath;
            }
        }

        // 4. Atalhos (.lnk) no Menu Iniciar
        foreach (var pattern in vendor.StartMenuPatterns)
        {
            var shortcut = FindStartMenuShortcut(pattern);
            if (!string.IsNullOrWhiteSpace(shortcut))
                return shortcut;
        }

        return null;
    }

    private string? TrySearchGenericApp(string rawApp)
    {
        var token = rawApp.Trim().Trim('"');
        var exeCandidate = token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? token : token + ".exe";

        if (_customProgramRoots is null)
        {
            var fromRegOrPath = TryFindInAppPathsOrSystemPath(exeCandidate);
            if (!string.IsNullOrWhiteSpace(fromRegOrPath))
                return fromRegOrPath;
        }

        return FindStartMenuShortcut(token);
    }

    private IReadOnlyList<string> GetProgramRoots()
    {
        if (_customProgramRoots is not null)
            return _customProgramRoots;

        var roots = new List<string>(5);
        void AddIfNotEmpty(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        AddIfNotEmpty(Environment.GetEnvironmentVariable("ProgramW6432"));
        AddIfNotEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfNotEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddIfNotEmpty(@"C:\Program Files");
        AddIfNotEmpty(@"C:\Program Files (x86)");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddIfNotEmpty(Path.Combine(localAppData, "Programs"));
            AddIfNotEmpty(localAppData);
        }

        return roots;
    }

    private IReadOnlyList<string> GetStartMenuRoots()
    {
        if (_customStartMenuRoots is not null)
            return _customStartMenuRoots;

        var roots = new List<string>(2);
        void AddIfExists(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(path);
        }

        AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        return roots;
    }

    private string? FindStartMenuShortcut(string keyword)
    {
        var normKeyword = CompactNormalize(keyword);
        if (normKeyword.Length < 3)
            return null;

        foreach (var root in GetStartMenuRoots())
        {
            try
            {
                foreach (var lnk in EnumerateFilesBounded(root, "*.lnk", maxDepth: 3))
                {
                    var nameNorm = CompactNormalize(Path.GetFileNameWithoutExtension(lnk));
                    if (nameNorm.Contains("uninstall", StringComparison.Ordinal) ||
                        nameNorm.Contains("desinstalar", StringComparison.Ordinal) ||
                        nameNorm.Contains("readme", StringComparison.Ordinal) ||
                        nameNorm.Contains("help", StringComparison.Ordinal) ||
                        nameNorm.Contains("manual", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (nameNorm.Contains(normKeyword, StringComparison.Ordinal))
                        return lnk;
                }
            }
            catch { }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFilesBounded(string directory, string pattern, int maxDepth)
    {
        if (maxDepth < 0 || !Directory.Exists(directory))
            yield break;

        string[] files = [];
        try { files = Directory.GetFiles(directory, pattern); } catch { }
        foreach (var f in files)
            yield return f;

        if (maxDepth == 0)
            yield break;

        string[] subdirs = [];
        try { subdirs = Directory.GetDirectories(directory); } catch { }
        foreach (var sub in subdirs)
        {
            foreach (var nested in EnumerateFilesBounded(sub, pattern, maxDepth - 1))
                yield return nested;
        }
    }

    private static string? TryFindInAppPathsOrSystemPath(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}", writable: false);
                var val = (key?.GetValue(null) as string)?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var expanded = Environment.ExpandEnvironmentVariables(val);
                    if (File.Exists(expanded))
                        return expanded;
                }
            }
            catch { }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildOptionalProfileArguments(string exePath, string rawVpnName)
    {
        if (string.IsNullOrWhiteSpace(rawVpnName) ||
            !Path.GetFileName(exePath).Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var cleanProfile = rawVpnName.Trim();
        var ovpnFile = cleanProfile.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)
            ? cleanProfile
            : cleanProfile + ".ovpn";

        if (ovpnFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Array.Empty<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateDirs = new[]
        {
            !string.IsNullOrWhiteSpace(userProfile) ? Path.Combine(userProfile, @"OpenVPN\config") : null,
            !string.IsNullOrWhiteSpace(userProfile) ? Path.Combine(userProfile, @"OpenVPN\config", Path.GetFileNameWithoutExtension(ovpnFile)) : null,
            @"C:\Program Files\OpenVPN\config"
        };

        foreach (var dir in candidateDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ovpnFile)))
            {
                return ["--command", "connect", ovpnFile];
            }
        }

        return Array.Empty<string>();
    }

    private string? BuildRunningTrayHintIfApplicable(string exePath, IReadOnlyList<string> args)
    {
        if (_customStarter is not null)
            return null;

        if (Path.GetFileName(exePath).Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase) && args.Count == 0)
        {
            try
            {
                var running = Process.GetProcessesByName("openvpn-gui");
                var anyAlive = false;
                foreach (var p in running)
                {
                    try { if (!p.HasExited) anyAlive = true; } catch { }
                    finally { p.Dispose(); }
                }

                if (anyAlive)
                {
                    return L.Text("OpenVPN GUI ativo na bandeja do Windows (clique com o botão direito no ícone perto do relógio para conectar).");
                }
            }
            catch { }
        }

        return null;
    }

    private static string? TryGetRunningProcessPath(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        var fileName = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName))
                            return fileName;
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        return null;
    }

    private static void TryFocusExistingProcessWindow(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        HotKeyNative.ShowWindow(proc.MainWindowHandle, HotKeyNative.SW_RESTORE);
                        HotKeyNative.SetForegroundWindow(proc.MainWindowHandle);
                        break;
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }
}
