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

/// <summary>
/// Credencial transitória em memória para acionamento de VPNs com suporte a autenticação via CLI.
/// ToString omite intencionalmente a senha para nunca vazar em logs ou diagnósticos.
/// </summary>
public sealed record VpnLoginCredential(string Username, string Domain, string Password)
{
    public override string ToString() => $"VpnLoginCredential(User={Username}, Domain={Domain})";
}

public sealed record DiscoveredVpnProfile(
    string ProfileName,
    string SuggestedApp,
    string? SuggestedServer = null,
    string? SourceDetail = null)
{
    public string MenuHeader => string.IsNullOrWhiteSpace(SuggestedServer)
        ? $"{ProfileName}  —  {SuggestedApp}"
        : $"{ProfileName}  —  {SuggestedApp} ({SuggestedServer})";
}

public sealed record VpnLaunchResolution(
    bool Found,
    string DisplayClientName,
    string TargetPathOrUri,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute,
    bool IsWindowsNativeFallback,
    string? HintMessage = null,
    bool PassedCredentialsDirectly = false,
    string? MatchedProfileName = null);

public sealed record VpnLaunchOutcome(
    bool Started,
    bool UsedWindowsFallback,
    string ClientDisplayName,
    string StatusMessage,
    string? ErrorMessage = null,
    bool PassedCredentialsDirectly = false,
    string? MatchedProfileName = null);

/// <summary>
/// Localiza, descobre perfis locais e aciona clientes de VPN heterogêneos (FortiClient, OpenVPN GUI/Connect,
/// OpenConnect GUI, Radmin VPN, Cisco Secure Client/AnyConnect, SonicWall NetExtender, GlobalProtect,
/// WireGuard, Tailscale, ZeroTier, Hamachi, Sophos, WatchGuard, SoftEther, Pritunl, VPN nativa do Windows
/// ou executáveis/perfis .ovpn personalizados).
/// </summary>
public sealed class VpnClientLauncherService
{
    public const string WindowsVpnSettingsUri = "ms-settings:network-vpn";

    public static IReadOnlyList<string> CommonVpnClientPresets { get; } =
    [
        "FortiClient",
        "OpenVPN GUI",
        "OpenVPN Connect",
        "OpenConnect GUI",
        "Radmin VPN",
        "Cisco Secure Client (AnyConnect)",
        "SonicWall NetExtender",
        "Palo Alto GlobalProtect",
        "WireGuard",
        "Tailscale",
        "ZeroTier",
        "LogMeIn Hamachi",
        "Sophos Connect",
        "WatchGuard Mobile VPN",
        "Check Point Endpoint",
        "Ivanti / Pulse Secure",
        "SoftEther VPN",
        "Pritunl",
        "Cloudflare WARP",
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
            DisplayName: "Radmin VPN",
            Keywords: ["radminvpn", "radmin", "rvrvpngui", "famatech"],
            ProcessNames: ["RvRvpnGui"],
            RelativeCandidatePaths:
            [
                @"Radmin VPN\RvRvpnGui.exe",
                @"Famatech\Radmin VPN\RvRvpnGui.exe",
                @"Radmin VPN\RadminVPN.exe",
                @"Famatech\Radmin VPN\RadminVPN.exe"
            ],
            StartMenuPatterns: ["Radmin VPN"]),

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
            DisplayName: "WireGuard",
            Keywords: ["wireguard"],
            ProcessNames: ["wireguard"],
            RelativeCandidatePaths:
            [
                @"WireGuard\wireguard.exe"
            ],
            StartMenuPatterns: ["WireGuard"]),

        new(
            DisplayName: "Tailscale",
            Keywords: ["tailscale"],
            ProcessNames: ["tailscale-ipn"],
            RelativeCandidatePaths:
            [
                @"Tailscale\tailscale-ipn.exe"
            ],
            StartMenuPatterns: ["Tailscale"]),

        new(
            DisplayName: "ZeroTier",
            Keywords: ["zerotier"],
            ProcessNames: ["zerotier_desktop_ui"],
            RelativeCandidatePaths:
            [
                @"ZeroTier\One\zerotier_desktop_ui.exe"
            ],
            StartMenuPatterns: ["ZeroTier"]),

        new(
            DisplayName: "LogMeIn Hamachi",
            Keywords: ["hamachi", "logmeinhamachi"],
            ProcessNames: ["hamachi-2-ui"],
            RelativeCandidatePaths:
            [
                @"LogMeIn Hamachi\hamachi-2-ui.exe",
                @"LogMeIn Hamachi\x64\hamachi-2-ui.exe"
            ],
            StartMenuPatterns: ["Hamachi"]),

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
            DisplayName: "Ivanti / Pulse Secure",
            Keywords: ["pulsesecure", "ivanti", "pulseui", "isac"],
            ProcessNames: ["Pulse", "PulseUI", "Ivanti"],
            RelativeCandidatePaths:
            [
                @"Common Files\Pulse Secure\JamUI\Pulse.exe",
                @"Pulse Secure\Pulse\Pulse.exe",
                @"Ivanti\Ivanti Secure Access Client\Pulse.exe"
            ],
            StartMenuPatterns: ["Pulse Secure", "Ivanti Secure"]),

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
            DisplayName: "Pritunl",
            Keywords: ["pritunl"],
            ProcessNames: ["pritunl", "Pritunl"],
            RelativeCandidatePaths:
            [
                @"Pritunl\pritunl.exe"
            ],
            StartMenuPatterns: ["Pritunl"]),

        new(
            DisplayName: "Cloudflare WARP",
            Keywords: ["cloudflarewarp", "cloudflare", "warp"],
            ProcessNames: ["Cloudflare WARP"],
            RelativeCandidatePaths:
            [
                @"Cloudflare\Cloudflare WARP\Cloudflare WARP.exe"
            ],
            StartMenuPatterns: ["Cloudflare WARP"])
    ];

    private static readonly string[] WindowsNativeKeywords =
    [
        "vpnwindows", "vpndowindows", "windows", "nativa", "rasphone", "rasdial", "discador", "l2tp", "pptp", "sstp", "ikev2"
    ];

    private static readonly HashSet<string> ProfileNoiseTokens = new(StringComparer.Ordinal)
    {
        "vpn", "openvpn", "openvpngui", "opengui", "openconnect", "openconect", "open", "gui",
        "connect", "client", "cliente", "ovpn", "acesso", "servidor", "srv", "prod", "producao",
        "homolog", "homologacao", "forticlient", "fortcliente", "fortclient", "radmin", "matriz", "filial"
    };

    private readonly AppLogger? _logger;
    private readonly Func<ProcessStartInfo, bool>? _customStarter;
    private readonly IReadOnlyList<string>? _customProgramRoots;
    private readonly IReadOnlyList<string>? _customStartMenuRoots;
    private readonly IReadOnlyList<string>? _customOpenVpnConfigRoots;
    private readonly IReadOnlyList<string>? _customRasPhonebookFiles;
    private readonly Func<string, bool>? _customIsProcessRunning;

    public VpnClientLauncherService(
        AppLogger? logger = null,
        Func<ProcessStartInfo, bool>? customStarter = null,
        IReadOnlyList<string>? customProgramRoots = null,
        IReadOnlyList<string>? customStartMenuRoots = null,
        IReadOnlyList<string>? customOpenVpnConfigRoots = null,
        IReadOnlyList<string>? customRasPhonebookFiles = null,
        Func<string, bool>? customIsProcessRunning = null)
    {
        _logger = logger;
        _customStarter = customStarter;
        _customProgramRoots = customProgramRoots;
        _customStartMenuRoots = customStartMenuRoots;
        _customOpenVpnConfigRoots = customOpenVpnConfigRoots;
        _customRasPhonebookFiles = customRasPhonebookFiles;
        _customIsProcessRunning = customIsProcessRunning;
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

    public static string ExtractProfileStem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var raw = value.Trim();
        if (raw.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^5];

        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            var word = current.ToString();
            current.Clear();
            if (!ProfileNoiseTokens.Contains(word))
                tokens.Add(word);
        }

        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                current.Append(char.ToLowerInvariant(ch));
            else
                Flush();
        }
        Flush();

        return tokens.Count > 0 ? string.Concat(tokens) : string.Empty;
    }

    public static string? TryExtractRemoteServerFromOvpnFile(string ovpnFilePath)
    {
        if (string.IsNullOrWhiteSpace(ovpnFilePath) || !File.Exists(ovpnFilePath))
            return null;

        try
        {
            foreach (var rawLine in File.ReadLines(ovpnFilePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                if (line.StartsWith("remote ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("remote\t", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
                        return $"{parts[1]}:{port}";
                    if (parts.Length >= 2)
                        return parts[1];
                }
            }
        }
        catch { }

        return null;
    }

    public IReadOnlyList<DiscoveredVpnProfile> DiscoverInstalledVpnProfiles()
    {
        var discovered = new List<DiscoveredVpnProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddProfile(DiscoveredVpnProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.ProfileName))
                return;
            var key = $"{profile.SuggestedApp}|{profile.ProfileName.Trim()}";
            if (seen.Add(key))
                discovered.Add(profile);
        }

        // 1. Perfis .ovpn (OpenVPN GUI)
        var openVpnVendor = KnownVendors.FirstOrDefault(v => v.DisplayName == "OpenVPN GUI");
        var openVpnExe = openVpnVendor is not null ? TryLocateVendorExecutableOrShortcut(openVpnVendor) : null;
        foreach (var ovpnFile in EnumerateOpenVpnProfileFiles(openVpnExe))
        {
            var baseName = Path.GetFileNameWithoutExtension(ovpnFile);
            var remote = TryExtractRemoteServerFromOvpnFile(ovpnFile);
            AddProfile(new DiscoveredVpnProfile(baseName, "OpenVPN GUI", remote, ovpnFile));
        }

        // 2. Perfis de VPN nativa do Windows (rasphone.pbk)
        foreach (var (entryName, phoneNumber) in ReadRasPhonebookEntries())
        {
            AddProfile(new DiscoveredVpnProfile(entryName, "VPN do Windows", phoneNumber));
        }

        // 3. Perfis do Registro (FortiClient e OpenConnect GUI) quando em execução normal no Windows
        if (_customProgramRoots is null)
        {
            DiscoverFortiClientRegistryProfiles(AddProfile);
            DiscoverOpenConnectRegistryProfiles(AddProfile);
        }

        return discovered
            .OrderBy(p => p.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SuggestedApp, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public VpnLaunchResolution Resolve(ConnectionEntry entry, VpnLoginCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var rawApp = (entry.VpnApp ?? string.Empty).Trim();
        var rawName = (entry.VpnName ?? string.Empty).Trim();
        var rawNotes = (entry.VpnNotes ?? string.Empty).Trim();

        // 0. Caso VpnApp ou VpnName aponte diretamente para um arquivo .ovpn existente
        var explicitOvpnPath = TryResolveExplicitOvpnFilePath(rawApp) ?? TryResolveExplicitOvpnFilePath(rawName);
        if (!string.IsNullOrWhiteSpace(explicitOvpnPath))
        {
            return BuildOpenVpnExplicitFileResolution(explicitOvpnPath);
        }

        // 1. Caminho explícito de executável/atalho informado em VpnApp
        if (!string.IsNullOrWhiteSpace(rawApp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawApp.Trim('"'));
            if (File.Exists(expanded))
            {
                var display = Path.GetFileNameWithoutExtension(expanded);
                return BuildExecutableResolution(
                    locatedPath: expanded,
                    displayName: string.IsNullOrWhiteSpace(display) ? rawApp : display,
                    entry: entry,
                    credential: credential);
            }
        }

        // 2. Verifica se é explicitamente VPN nativa do Windows
        var normApp = CompactNormalize(rawApp);
        var normCombined = CompactNormalize($"{rawApp} {rawName} {rawNotes}");

        if (!string.IsNullOrEmpty(normApp) && WindowsNativeKeywords.Any(normApp.Contains))
        {
            return BuildWindowsNativeResolution(rawName, credential);
        }

        // 3. Identificação tolerante a variações ("fortcliente", "open connect", "open gui", "radmin", etc.)
        var matchedVendor = FindVendorByText(normApp) ?? FindVendorByText(normCombined);
        if (matchedVendor is not null)
        {
            var located = TryLocateVendorExecutableOrShortcut(matchedVendor);
            if (!string.IsNullOrWhiteSpace(located))
            {
                return BuildExecutableResolution(
                    locatedPath: located,
                    displayName: matchedVendor.DisplayName,
                    entry: entry,
                    credential: credential);
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
                return BuildExecutableResolution(
                    locatedPath: genericMatch,
                    displayName: rawApp,
                    entry: entry,
                    credential: credential);
            }

            return new VpnLaunchResolution(
                Found: false,
                DisplayClientName: rawApp,
                TargetPathOrUri: string.Empty,
                Arguments: Array.Empty<string>(),
                UseShellExecute: false,
                IsWindowsNativeFallback: false);
        }

        // 5. Se VpnApp estiver em branco: se houver perfil .ovpn correspondente ao nome, prioriza OpenVPN GUI; senão VPN do Windows
        var openVpnVendor = KnownVendors.First(v => v.DisplayName == "OpenVPN GUI");
        var openVpnExe = TryLocateVendorExecutableOrShortcut(openVpnVendor);
        if (!string.IsNullOrWhiteSpace(openVpnExe) &&
            Path.GetFileName(openVpnExe).Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase))
        {
            var matchedOvpn = TryMatchOpenVpnProfileFile(openVpnExe, entry);
            if (!string.IsNullOrWhiteSpace(matchedOvpn))
            {
                return BuildOpenVpnGuiProfileResolution(openVpnExe, matchedOvpn);
            }
        }

        return BuildWindowsNativeResolution(rawName, credential);
    }

    public VpnLaunchOutcome Launch(ConnectionEntry entry, VpnLoginCredential? credential = null)
    {
        var resolution = Resolve(entry, credential);
        if (!resolution.Found)
        {
            return new VpnLaunchOutcome(
                Started: false,
                UsedWindowsFallback: false,
                ClientDisplayName: resolution.DisplayClientName,
                StatusMessage: L.Format("Cliente VPN '{0}' não localizado automaticamente.", resolution.DisplayClientName),
                ErrorMessage: L.Format(
                    "Não foi possível localizar o cliente VPN '{0}' neste computador.\n\nDica: no bloco 'VPN do cliente', use o botão '...' ao lado de Aplicativo para indicar o executável (ou arquivo .ovpn) ou abra as Configurações de VPN do Windows.",
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

    private VpnLaunchResolution BuildExecutableResolution(
        string locatedPath,
        string displayName,
        ConnectionEntry entry,
        VpnLoginCredential? credential)
    {
        var exeFileName = Path.GetFileName(locatedPath);

        // OpenVPN GUI: aciona diretamente o perfil .ovpn correspondente (--command connect quando aberto, --connect quando fechado)
        if (exeFileName.Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase))
        {
            var matchedOvpn = TryMatchOpenVpnProfileFile(locatedPath, entry);
            if (!string.IsNullOrWhiteSpace(matchedOvpn))
            {
                return BuildOpenVpnGuiProfileResolution(locatedPath, matchedOvpn);
            }

            var trayHint = IsProcessCurrentlyRunning("openvpn-gui")
                ? L.Text("OpenVPN GUI ativo na bandeja do Windows (use a seta '▾' em Nome da VPN para vincular o perfil .ovpn e conectar direto).")
                : null;

            return new VpnLaunchResolution(
                Found: true,
                DisplayClientName: displayName,
                TargetPathOrUri: locatedPath,
                Arguments: Array.Empty<string>(),
                UseShellExecute: false,
                IsWindowsNativeFallback: false,
                HintMessage: trayHint);
        }

        // SonicWall NetExtender: se houver credencial e NECLI.exe ao lado do NEGui.exe + Servidor VPN preenchido, conecta via CLI
        if (exeFileName.Equals("NEGui.exe", StringComparison.OrdinalIgnoreCase) &&
            credential is not null &&
            !string.IsNullOrWhiteSpace(credential.Username) &&
            !string.IsNullOrEmpty(credential.Password) &&
            !string.IsNullOrWhiteSpace(entry.VpnServer))
        {
            var dir = Path.GetDirectoryName(locatedPath);
            var neCli = !string.IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "NECLI.exe") : null;
            if (!string.IsNullOrWhiteSpace(neCli) && File.Exists(neCli))
            {
                var cliArgs = new List<string>
                {
                    "connect",
                    "-s", entry.VpnServer.Trim(),
                    "-u", credential.Username.Trim(),
                    "-p", credential.Password
                };
                if (!string.IsNullOrWhiteSpace(credential.Domain))
                {
                    cliArgs.Add("-d");
                    cliArgs.Add(credential.Domain.Trim());
                }

                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: displayName,
                    TargetPathOrUri: neCli,
                    Arguments: cliArgs,
                    UseShellExecute: false,
                    IsWindowsNativeFallback: false,
                    HintMessage: L.Format("Conectando SonicWall NetExtender em {0}...", entry.VpnServer.Trim()),
                    PassedCredentialsDirectly: true);
            }
        }

        var useShell = !locatedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: displayName,
            TargetPathOrUri: locatedPath,
            Arguments: Array.Empty<string>(),
            UseShellExecute: useShell,
            IsWindowsNativeFallback: false);
    }

    private VpnLaunchResolution BuildOpenVpnExplicitFileResolution(string ovpnFilePath)
    {
        var openVpnGuiExe = FindSpecificExecutableAmongVendors("openvpn-gui.exe");
        if (!string.IsNullOrWhiteSpace(openVpnGuiExe))
        {
            return BuildOpenVpnGuiProfileResolution(openVpnGuiExe, ovpnFilePath);
        }

        var baseName = Path.GetFileNameWithoutExtension(ovpnFilePath);
        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: $"OpenVPN ({baseName})",
            TargetPathOrUri: ovpnFilePath,
            Arguments: Array.Empty<string>(),
            UseShellExecute: true,
            IsWindowsNativeFallback: false,
            HintMessage: L.Format("Abrindo perfil VPN '{0}'...", baseName),
            MatchedProfileName: baseName);
    }

    private VpnLaunchResolution BuildOpenVpnGuiProfileResolution(string openVpnGuiExePath, string ovpnFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(ovpnFilePath);
        var fileName = Path.GetFileName(ovpnFilePath);
        var alreadyRunning = IsProcessCurrentlyRunning("openvpn-gui");

        // OpenVPN GUI aceita:
        // - Quando já está aberto na bandeja: --command connect <nome_sem_extensao>
        // - Quando está fechado: --connect <arquivo.ovpn>
        IReadOnlyList<string> args = alreadyRunning
            ? ["--command", "connect", baseName]
            : ["--connect", fileName];

        var hint = alreadyRunning
            ? L.Format("Conectando perfil '{0}' no OpenVPN GUI...", baseName)
            : L.Format("Iniciando OpenVPN GUI e conectando perfil '{0}'...", baseName);

        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: $"OpenVPN GUI ({baseName})",
            TargetPathOrUri: openVpnGuiExePath,
            Arguments: args,
            UseShellExecute: false,
            IsWindowsNativeFallback: false,
            HintMessage: hint,
            MatchedProfileName: baseName);
    }

    private VpnLaunchOutcome ExecuteResolution(VpnLaunchResolution resolution)
    {
        try
        {
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

            // Nunca registra argumentos no log para preservar segredos em chamadas CLI
            _logger?.Info("VPN", $"Cliente VPN acionado: {resolution.DisplayClientName}");
            return new VpnLaunchOutcome(
                Started: true,
                UsedWindowsFallback: resolution.IsWindowsNativeFallback,
                ClientDisplayName: resolution.DisplayClientName,
                StatusMessage: status,
                PassedCredentialsDirectly: resolution.PassedCredentialsDirectly,
                MatchedProfileName: resolution.MatchedProfileName);
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

    private VpnLaunchResolution BuildWindowsNativeResolution(string rawVpnName, VpnLoginCredential? credential)
    {
        var cleanName = rawVpnName.Trim();
        if (!string.IsNullOrWhiteSpace(cleanName) && RasPhonebookContainsEntry(cleanName))
        {
            var rasdial = FindSystemOrRootExecutable("rasdial.exe");
            if (!string.IsNullOrWhiteSpace(rasdial) &&
                credential is not null &&
                !string.IsNullOrWhiteSpace(credential.Username) &&
                !string.IsNullOrEmpty(credential.Password))
            {
                var formattedUser = string.IsNullOrWhiteSpace(credential.Domain)
                    ? credential.Username.Trim()
                    : $"{credential.Domain.Trim()}\\{credential.Username.Trim()}";

                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"VPN do Windows ({cleanName})",
                    TargetPathOrUri: rasdial,
                    Arguments: [cleanName, formattedUser, credential.Password],
                    UseShellExecute: false,
                    IsWindowsNativeFallback: true,
                    HintMessage: L.Format("Conectando VPN do Windows '{0}'...", cleanName),
                    PassedCredentialsDirectly: true,
                    MatchedProfileName: cleanName);
            }

            var rasphone = FindSystemOrRootExecutable("rasphone.exe");
            if (!string.IsNullOrWhiteSpace(rasphone))
            {
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"VPN do Windows ({cleanName})",
                    TargetPathOrUri: rasphone,
                    Arguments: ["-d", cleanName],
                    UseShellExecute: false,
                    IsWindowsNativeFallback: true,
                    HintMessage: L.Format("Abrindo discador da VPN '{0}'...", cleanName),
                    MatchedProfileName: cleanName);
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

    private string? FindSystemOrRootExecutable(string exeName)
    {
        foreach (var root in GetProgramRoots())
        {
            var customSys = Path.Combine(root, exeName);
            if (File.Exists(customSys))
                return customSys;
        }

        if (_customProgramRoots is null)
        {
            var sysDir = Environment.SystemDirectory;
            if (!string.IsNullOrWhiteSpace(sysDir))
            {
                var candidate = Path.Combine(sysDir, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? TryResolveExplicitOvpnFilePath(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var trimmed = rawValue.Trim().Trim('"');
        if (!trimmed.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return File.Exists(expanded) ? expanded : null;
    }

    private string? FindSpecificExecutableAmongVendors(string targetExeName)
    {
        foreach (var root in GetProgramRoots())
        {
            foreach (var vendor in KnownVendors)
            {
                foreach (var rel in vendor.RelativeCandidatePaths)
                {
                    if (!Path.GetFileName(rel.Replace('\\', Path.DirectorySeparatorChar))
                            .Equals(targetExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        if (_customProgramRoots is null)
        {
            var running = TryGetRunningProcessPath(Path.GetFileNameWithoutExtension(targetExeName));
            if (!string.IsNullOrWhiteSpace(running) && File.Exists(running))
                return running;

            return TryFindInAppPathsOrSystemPath(targetExeName);
        }

        return null;
    }

    private string? TryMatchOpenVpnProfileFile(string openVpnGuiExePath, ConnectionEntry entry)
    {
        var files = EnumerateOpenVpnProfileFiles(openVpnGuiExePath);
        if (files.Count == 0)
            return null;

        var rawVpnName = (entry.VpnName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(rawVpnName))
        {
            var withoutExt = rawVpnName.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)
                ? rawVpnName[..^5].Trim()
                : rawVpnName;

            // 1. Correspondência exata pelo nome base do arquivo .ovpn
            var exact = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(withoutExt, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return exact;

            // 2. Correspondência normalizada compacta exata
            var normTarget = CompactNormalize(withoutExt);
            if (!string.IsNullOrEmpty(normTarget))
            {
                var compactExact = files.FirstOrDefault(f =>
                    CompactNormalize(Path.GetFileNameWithoutExtension(f)).Equals(normTarget, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(compactExact))
                    return compactExact;
            }

            // 3. Correspondência por radical limpo (ex.: "Polividros OpenGui" -> "polividros" -> "Polividros.ovpn")
            var stemMatch = TryMatchUniqueByStem(files, rawVpnName);
            if (!string.IsNullOrWhiteSpace(stemMatch))
                return stemMatch;
        }

        // 4. Caso Nome da VPN esteja vazio ou genérico ("OpenGui"), tenta pelo Cliente ou Nome da conexão se unívoco
        foreach (var fallbackCandidate in new[] { entry.ClientName, entry.Name })
        {
            if (string.IsNullOrWhiteSpace(fallbackCandidate))
                continue;
            var match = TryMatchUniqueByStem(files, fallbackCandidate, exactStemOnly: true);
            if (!string.IsNullOrWhiteSpace(match))
                return match;
        }

        return null;
    }

    private static string? TryMatchUniqueByStem(IReadOnlyList<string> ovpnFiles, string candidateLabel, bool exactStemOnly = false)
    {
        var targetStem = ExtractProfileStem(candidateLabel);
        if (targetStem.Length < 3)
            return null;

        var exactStemMatches = ovpnFiles
            .Where(f => ExtractProfileStem(Path.GetFileNameWithoutExtension(f)).Equals(targetStem, StringComparison.Ordinal))
            .ToList();

        if (exactStemMatches.Select(f => Path.GetFileNameWithoutExtension(f)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return exactStemMatches[0];

        if (exactStemOnly || targetStem.Length < 4)
            return null;

        var partialMatches = ovpnFiles
            .Where(f =>
            {
                var fileStem = ExtractProfileStem(Path.GetFileNameWithoutExtension(f));
                return fileStem.Length >= 4 &&
                       (targetStem.Contains(fileStem, StringComparison.Ordinal) ||
                        fileStem.Contains(targetStem, StringComparison.Ordinal));
            })
            .ToList();

        if (partialMatches.Select(f => Path.GetFileNameWithoutExtension(f)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return partialMatches[0];

        return null;
    }

    private IReadOnlyList<string> EnumerateOpenVpnProfileFiles(string? openVpnExePath)
    {
        var files = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configDir in GetOpenVpnConfigDirectories(openVpnExePath))
        {
            foreach (var ovpn in EnumerateFilesBounded(configDir, "*.ovpn", maxDepth: 2))
            {
                if (seenPaths.Add(ovpn))
                    files.Add(ovpn);
            }
        }

        return files;
    }

    private IReadOnlyList<string> GetOpenVpnConfigDirectories(string? openVpnExePath)
    {
        var dirs = new List<string>();
        void AddDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            foreach (var segment in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"'));
                if (Directory.Exists(expanded) && !dirs.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(expanded);
            }
        }

        if (_customOpenVpnConfigRoots is not null)
        {
            foreach (var root in _customOpenVpnConfigRoots)
                AddDir(root);
            return dirs;
        }

        // Diretório ../config relativo ao openvpn-gui.exe localizado (cobre instalações customizadas e testes)
        if (!string.IsNullOrWhiteSpace(openVpnExePath))
        {
            try
            {
                var binDir = Path.GetDirectoryName(openVpnExePath);
                var openVpnRoot = !string.IsNullOrWhiteSpace(binDir) ? Directory.GetParent(binDir)?.FullName : null;
                if (!string.IsNullOrWhiteSpace(openVpnRoot))
                    AddDir(Path.Combine(openVpnRoot, "config"));
            }
            catch { }
        }

        foreach (var progRoot in GetProgramRoots())
        {
            AddDir(Path.Combine(progRoot, "OpenVPN", "config"));
        }

        if (_customProgramRoots is null)
        {
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\OpenVPN-GUI", writable: false);
                AddDir(hkcu?.GetValue("config_dir") as string);
            }
            catch { }

            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenVPN", writable: false);
                AddDir(hklm?.GetValue("config_dir") as string);
            }
            catch { }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
                AddDir(Path.Combine(userProfile, "OpenVPN", "config"));

            AddDir(@"C:\Program Files\OpenVPN\config");
            AddDir(@"C:\Program Files (x86)\OpenVPN\config");
        }

        return dirs;
    }

    private bool RasPhonebookContainsEntry(string entryName)
    {
        var target = entryName.Trim();
        return ReadRasPhonebookEntries().Any(e => e.EntryName.Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<(string EntryName, string? PhoneNumber)> ReadRasPhonebookEntries()
    {
        var results = new List<(string, string?)>();
        IEnumerable<string> pbkFiles;

        if (_customRasPhonebookFiles is not null)
        {
            pbkFiles = _customRasPhonebookFiles;
        }
        else if (_customProgramRoots is not null)
        {
            pbkFiles = Array.Empty<string>();
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            pbkFiles = new[]
            {
                !string.IsNullOrWhiteSpace(appData) ? Path.Combine(appData, @"Microsoft\Network\Connections\Pbk\rasphone.pbk") : string.Empty,
                !string.IsNullOrWhiteSpace(commonAppData) ? Path.Combine(commonAppData, @"Microsoft\Network\Connections\Pbk\rasphone.pbk") : string.Empty
            };
        }

        foreach (var pbk in pbkFiles)
        {
            if (string.IsNullOrWhiteSpace(pbk) || !File.Exists(pbk))
                continue;

            try
            {
                string? currentSection = null;
                string? currentServer = null;

                void FlushSection()
                {
                    if (!string.IsNullOrWhiteSpace(currentSection))
                        results.Add((currentSection, currentServer));
                    currentSection = null;
                    currentServer = null;
                }

                foreach (var raw in File.ReadLines(pbk))
                {
                    var line = raw.Trim();
                    if (line.Length > 2 && line.StartsWith('[') && line.EndsWith(']'))
                    {
                        FlushSection();
                        currentSection = line[1..^1].Trim();
                    }
                    else if (currentSection is not null &&
                             line.StartsWith("PhoneNumber=", StringComparison.OrdinalIgnoreCase) &&
                             string.IsNullOrWhiteSpace(currentServer))
                    {
                        var val = line["PhoneNumber=".Length..].Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                            currentServer = val;
                    }
                }
                FlushSection();
            }
            catch { }
        }

        return results;
    }

    private static void DiscoverFortiClientRegistryProfiles(Action<DiscoveredVpnProfile> addProfile)
    {
        var subKeys = new[]
        {
            @"SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels",
            @"SOFTWARE\WOW6432Node\Fortinet\FortiClient\Sslvpn\Tunnels"
        };

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var sub in subKeys)
            {
                try
                {
                    using var tunnelsKey = hive.OpenSubKey(sub, writable: false);
                    if (tunnelsKey is null) continue;
                    foreach (var name in tunnelsKey.GetSubKeyNames())
                    {
                        using var tunnel = tunnelsKey.OpenSubKey(name, writable: false);
                        var server = (tunnel?.GetValue("Server") as string)?.Trim();
                        addProfile(new DiscoveredVpnProfile(name, "FortiClient", server));
                    }
                }
                catch { }
            }
        }
    }

    private static void DiscoverOpenConnectRegistryProfiles(Action<DiscoveredVpnProfile> addProfile)
    {
        var subKeys = new[]
        {
            @"Software\OpenConnect-GUI Team\OpenConnect-GUI\vpn",
            @"Software\OpenConnect-GUI\vpn"
        };

        foreach (var sub in subKeys)
        {
            try
            {
                using var vpnKey = Registry.CurrentUser.OpenSubKey(sub, writable: false);
                if (vpnKey is null) continue;
                foreach (var name in vpnKey.GetSubKeyNames())
                {
                    using var profileKey = vpnKey.OpenSubKey(name, writable: false);
                    var server = (profileKey?.GetValue("server") as string)?.Trim();
                    addProfile(new DiscoveredVpnProfile(Uri.UnescapeDataString(name), "OpenConnect GUI", server));
                }
            }
            catch { }
        }
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
        if (_customProgramRoots is null)
        {
            foreach (var procName in vendor.ProcessNames)
            {
                var runningPath = TryGetRunningProcessPath(procName);
                if (!string.IsNullOrWhiteSpace(runningPath) && File.Exists(runningPath))
                    return runningPath;
            }
        }

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

        var roots = new List<string>(7);
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

    private bool IsProcessCurrentlyRunning(string processName)
    {
        if (_customIsProcessRunning is not null)
            return _customIsProcessRunning(processName);

        if (_customStarter is not null)
            return false;

        try
        {
            var running = Process.GetProcessesByName(processName);
            var anyAlive = false;
            foreach (var p in running)
            {
                try { if (!p.HasExited) anyAlive = true; } catch { }
                finally { p.Dispose(); }
            }
            return anyAlive;
        }
        catch
        {
            return false;
        }
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
