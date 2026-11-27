using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
/// Credencial manual transitória em memória para acionamento de VPNs.
/// ToString omite intencionalmente a senha para nunca vazar em logs ou diagnósticos.
/// </summary>
public sealed record VpnLoginCredential(string Username, string Domain, string Password)
{
    public string FormattedUsername => string.IsNullOrWhiteSpace(Domain)
        ? (Username ?? string.Empty).Trim()
        : $"{Domain.Trim()}\\{(Username ?? string.Empty).Trim()}";

    public bool HasAnyValue => !string.IsNullOrWhiteSpace(FormattedUsername) || !string.IsNullOrEmpty(Password);

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
    string? MatchedProfileName = null,
    VpnAutomationVendorKind AutomationVendor = VpnAutomationVendorKind.None,
    string? StandardInputPayload = null)
{
    // Sobrescreve ToString para garantir que StandardInputPayload e senhas em Arguments nunca apareçam em logs
    public override string ToString() => $"VpnLaunchResolution(Found={Found}, Client={DisplayClientName}, Profile={MatchedProfileName})";
}

public sealed record VpnLaunchOutcome(
    bool Started,
    bool UsedWindowsFallback,
    string ClientDisplayName,
    string StatusMessage,
    string? ErrorMessage = null,
    bool PassedCredentialsDirectly = false,
    string? MatchedProfileName = null,
    bool UiAutomationHandled = false);

/// <summary>
/// Localiza, descobre perfis locais, restaura janelas minimizadas na bandeja e aciona cada família de VPN
/// conforme sua mecânica real no Windows (CLI nativa, pipe stdin, IPC .ovpn ou Windows UI Automation).
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
        string[] WindowProcessNames,
        string[] RelativeCandidatePaths,
        string[] StartMenuPatterns,
        VpnAutomationVendorKind AutomationKind = VpnAutomationVendorKind.None);

    private static readonly KnownVpnVendor[] KnownVendors =
    [
        new(
            DisplayName: "FortiClient",
            Keywords: ["forticlient", "fortcliente", "fortclient", "fortinet", "fortivpn", "fortitray", "forti"],
            // Nunca incluir FortiTray aqui: FortiTray.exe é apenas o controlador da bandeja e não abre a janela!
            WindowProcessNames: ["FortiClientConsole"],
            RelativeCandidatePaths:
            [
                @"Fortinet\FortiClient\FortiClientConsole.exe",
                @"Fortinet\FortiClient\FortiClient.exe"
            ],
            StartMenuPatterns: ["FortiClient"],
            AutomationKind: VpnAutomationVendorKind.FortiClient),

        new(
            DisplayName: "OpenConnect GUI",
            Keywords: ["openconnect", "openconect", "openconnectgui"],
            WindowProcessNames: ["openconnect-gui"],
            RelativeCandidatePaths:
            [
                @"OpenConnect-GUI\openconnect-gui.exe",
                @"OpenConnect\openconnect-gui.exe"
            ],
            StartMenuPatterns: ["OpenConnect"],
            AutomationKind: VpnAutomationVendorKind.OpenConnectGui),

        new(
            DisplayName: "OpenVPN Connect",
            Keywords: ["openvpnconnect"],
            WindowProcessNames: ["OpenVPNConnect"],
            RelativeCandidatePaths:
            [
                @"OpenVPN Connect\OpenVPNConnect.exe",
                @"OpenVPN Technologies\OpenVPN Connect\OpenVPNConnect.exe"
            ],
            StartMenuPatterns: ["OpenVPN Connect"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "OpenVPN GUI",
            Keywords: ["openvpngui", "opengui", "openvpn", "ovpn"],
            WindowProcessNames: ["openvpn-gui", "OpenVPNConnect"],
            RelativeCandidatePaths:
            [
                @"OpenVPN\bin\openvpn-gui.exe",
                @"OpenVPN Connect\OpenVPNConnect.exe",
                @"OpenConnect-GUI\openconnect-gui.exe"
            ],
            StartMenuPatterns: ["OpenVPN", "OpenConnect"],
            AutomationKind: VpnAutomationVendorKind.OpenVpnGuiAuthDialog),

        new(
            DisplayName: "Radmin VPN",
            Keywords: ["radminvpn", "radmin", "rvrvpngui", "famatech"],
            // Nunca incluir RvControlSvc (serviço Windows); apenas a interface RvRvpnGui
            WindowProcessNames: ["RvRvpnGui"],
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
            WindowProcessNames: ["csc_ui", "vpnui"],
            RelativeCandidatePaths:
            [
                @"Cisco\Cisco Secure Client\UI\csc_ui.exe",
                @"Cisco\Cisco AnyConnect Secure Mobility Client\vpnui.exe"
            ],
            StartMenuPatterns: ["Cisco Secure Client", "AnyConnect"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "SonicWall NetExtender",
            Keywords: ["netextender", "sonicwall", "negui", "mobileconnect"],
            WindowProcessNames: ["NEGui", "NetExtender"],
            RelativeCandidatePaths:
            [
                @"SonicWall\SSL-VPN\NetExtender\NEGui.exe",
                @"SonicWALL\SSL-VPN\NetExtender\NEGui.exe"
            ],
            StartMenuPatterns: ["NetExtender", "SonicWall"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "Palo Alto GlobalProtect",
            Keywords: ["globalprotect", "paloalto", "pangpa"],
            WindowProcessNames: ["PanGPA"],
            RelativeCandidatePaths:
            [
                @"Palo Alto Networks\GlobalProtect\PanGPA.exe"
            ],
            StartMenuPatterns: ["GlobalProtect"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "WireGuard",
            Keywords: ["wireguard"],
            WindowProcessNames: ["wireguard"],
            RelativeCandidatePaths:
            [
                @"WireGuard\wireguard.exe"
            ],
            StartMenuPatterns: ["WireGuard"]),

        new(
            DisplayName: "Tailscale",
            Keywords: ["tailscale"],
            WindowProcessNames: ["tailscale-ipn"],
            RelativeCandidatePaths:
            [
                @"Tailscale\tailscale-ipn.exe"
            ],
            StartMenuPatterns: ["Tailscale"]),

        new(
            DisplayName: "ZeroTier",
            Keywords: ["zerotier"],
            WindowProcessNames: ["zerotier_desktop_ui"],
            RelativeCandidatePaths:
            [
                @"ZeroTier\One\zerotier_desktop_ui.exe"
            ],
            StartMenuPatterns: ["ZeroTier"]),

        new(
            DisplayName: "LogMeIn Hamachi",
            Keywords: ["hamachi", "logmeinhamachi"],
            WindowProcessNames: ["hamachi-2-ui"],
            RelativeCandidatePaths:
            [
                @"LogMeIn Hamachi\hamachi-2-ui.exe",
                @"LogMeIn Hamachi\x64\hamachi-2-ui.exe"
            ],
            StartMenuPatterns: ["Hamachi"]),

        new(
            DisplayName: "Sophos Connect",
            Keywords: ["sophos", "scgui"],
            WindowProcessNames: ["scgui"],
            RelativeCandidatePaths:
            [
                @"Sophos\Connect\GUI\scgui.exe",
                @"Sophos\Sophos SSL VPN Client\bin\openvpn-gui.exe"
            ],
            StartMenuPatterns: ["Sophos Connect", "Sophos SSL VPN"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "WatchGuard Mobile VPN",
            Keywords: ["watchguard", "wgsslvpnc"],
            WindowProcessNames: ["wgsslvpnc"],
            RelativeCandidatePaths:
            [
                @"WatchGuard\WatchGuard Mobile VPN with SSL\wgsslvpnc.exe"
            ],
            StartMenuPatterns: ["WatchGuard"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "Check Point Endpoint VPN",
            Keywords: ["checkpoint", "capsulevpn", "trgui", "endpointconnect"],
            WindowProcessNames: ["TrGUI"],
            RelativeCandidatePaths:
            [
                @"CheckPoint\Endpoint Connect\TrGUI.exe",
                @"CheckPoint\Endpoint Security\Endpoint Connect\TrGUI.exe"
            ],
            StartMenuPatterns: ["Endpoint Connect", "Check Point"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "Ivanti / Pulse Secure",
            Keywords: ["pulsesecure", "ivanti", "pulseui", "isac"],
            WindowProcessNames: ["Pulse", "PulseUI", "Ivanti"],
            RelativeCandidatePaths:
            [
                @"Common Files\Pulse Secure\JamUI\Pulse.exe",
                @"Pulse Secure\Pulse\Pulse.exe",
                @"Ivanti\Ivanti Secure Access Client\Pulse.exe"
            ],
            StartMenuPatterns: ["Pulse Secure", "Ivanti Secure"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "SoftEther VPN",
            Keywords: ["softether", "vpncmgr"],
            WindowProcessNames: ["vpncmgr", "vpncmgr_x64"],
            RelativeCandidatePaths:
            [
                @"SoftEther VPN Client\vpncmgr_x64.exe",
                @"SoftEther VPN Client\vpncmgr.exe"
            ],
            StartMenuPatterns: ["SoftEther"]),

        new(
            DisplayName: "Pritunl",
            Keywords: ["pritunl"],
            WindowProcessNames: ["pritunl", "Pritunl"],
            RelativeCandidatePaths:
            [
                @"Pritunl\pritunl.exe"
            ],
            StartMenuPatterns: ["Pritunl"],
            AutomationKind: VpnAutomationVendorKind.GenericGui),

        new(
            DisplayName: "Cloudflare WARP",
            Keywords: ["cloudflarewarp", "cloudflare", "warp"],
            WindowProcessNames: ["Cloudflare WARP"],
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
    private readonly Action<string>? _customStdinObserver;
    private readonly IReadOnlyList<string>? _customProgramRoots;
    private readonly IReadOnlyList<string>? _customStartMenuRoots;
    private readonly IReadOnlyList<string>? _customOpenVpnConfigRoots;
    private readonly IReadOnlyList<string>? _customRasPhonebookFiles;
    private readonly Func<string, bool>? _customIsProcessRunning;
    private readonly VpnWindowAutomator _windowAutomator;

    public VpnClientLauncherService(
        AppLogger? logger = null,
        Func<ProcessStartInfo, bool>? customStarter = null,
        IReadOnlyList<string>? customProgramRoots = null,
        IReadOnlyList<string>? customStartMenuRoots = null,
        IReadOnlyList<string>? customOpenVpnConfigRoots = null,
        IReadOnlyList<string>? customRasPhonebookFiles = null,
        Func<string, bool>? customIsProcessRunning = null,
        Action<string>? customStdinObserver = null)
    {
        _logger = logger;
        _customStarter = customStarter;
        _customProgramRoots = customProgramRoots;
        _customStartMenuRoots = customStartMenuRoots;
        _customOpenVpnConfigRoots = customOpenVpnConfigRoots;
        _customRasPhonebookFiles = customRasPhonebookFiles;
        _customIsProcessRunning = customIsProcessRunning;
        _customStdinObserver = customStdinObserver;
        _windowAutomator = new VpnWindowAutomator(logger);
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

        // 3. Perfis do Registro e clientes instalados no Windows (FortiClient SSL+IPsec, OpenConnect, Cisco, OpenVPN Connect)
        if (_customProgramRoots is null)
        {
            DiscoverFortiClientRegistryProfiles(AddProfile);
            DiscoverOpenConnectRegistryProfiles(AddProfile);
            DiscoverCiscoXmlProfiles(AddProfile);
            DiscoverOpenVpnConnectProfiles(AddProfile);
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
            return BuildOpenVpnExplicitFileResolution(explicitOvpnPath, credential);
        }

        // 1. Caminho explícito de executável/atalho informado em VpnApp
        if (!string.IsNullOrWhiteSpace(rawApp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawApp.Trim('"'));
            if (File.Exists(expanded))
            {
                var display = Path.GetFileNameWithoutExtension(expanded);
                var inferredVendor = FindVendorByText(CompactNormalize(display));
                return BuildExecutableResolution(
                    locatedPath: expanded,
                    displayName: inferredVendor?.DisplayName ?? (string.IsNullOrWhiteSpace(display) ? rawApp : display),
                    entry: entry,
                    credential: credential,
                    vendor: inferredVendor);
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
                    credential: credential,
                    vendor: matchedVendor);
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
                    credential: credential,
                    vendor: null);
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
                return BuildOpenVpnGuiProfileResolution(openVpnExe, matchedOvpn, credential);
            }
        }

        return BuildWindowsNativeResolution(rawName, credential);
    }

    public VpnLaunchOutcome Launch(ConnectionEntry entry, VpnLoginCredential? credential = null)
    {
        var resolution = Resolve(entry, credential);
        if (!resolution.Found)
            return BuildNotFoundOutcome(resolution);

        return ExecuteResolution(resolution);
    }

    public async Task<VpnLaunchOutcome> LaunchAsync(
        ConnectionEntry entry,
        VpnLoginCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = Resolve(entry, credential);
        if (!resolution.Found)
            return BuildNotFoundOutcome(resolution);

        var outcome = ExecuteResolution(resolution);
        if (!outcome.Started || _customStarter is not null || resolution.AutomationVendor == VpnAutomationVendorKind.None)
            return outcome;

        // Caso seja VPN do Windows sem senha informada e rasdial tenha sido acionado, fallback automático para rasphone se falhar
        var profileTarget = !string.IsNullOrWhiteSpace(resolution.MatchedProfileName)
            ? resolution.MatchedProfileName
            : (!string.IsNullOrWhiteSpace(entry.VpnName) ? entry.VpnName.Trim() : null);

        var uia = await _windowAutomator.AutomateAsync(
            resolution.AutomationVendor,
            profileTarget,
            credential,
            cancellationToken);

        if (uia.AlreadyConnected && !string.IsNullOrWhiteSpace(uia.DetailMessage))
        {
            return outcome with { StatusMessage = uia.DetailMessage!, UiAutomationHandled = true };
        }

        if (uia.ConnectInvoked)
        {
            var connectedMsg = !string.IsNullOrWhiteSpace(profileTarget)
                ? L.Format("Conectando '{0}' em {1}...", profileTarget, resolution.DisplayClientName)
                : L.Format("Conexão acionada em {0}...", resolution.DisplayClientName);
            return outcome with { StatusMessage = connectedMsg, UiAutomationHandled = true };
        }

        if (uia.ProfileSelected || uia.CredentialsFilled)
        {
            var preppedMsg = !string.IsNullOrWhiteSpace(profileTarget)
                ? L.Format("{0} aberto com perfil '{1}' selecionado.", resolution.DisplayClientName, profileTarget)
                : outcome.StatusMessage;
            return outcome with { StatusMessage = preppedMsg, UiAutomationHandled = true };
        }

        return outcome;
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

    private static VpnLaunchOutcome BuildNotFoundOutcome(VpnLaunchResolution resolution)
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

    private VpnLaunchResolution BuildExecutableResolution(
        string locatedPath,
        string displayName,
        ConnectionEntry entry,
        VpnLoginCredential? credential,
        KnownVpnVendor? vendor)
    {
        var exeFileName = Path.GetFileName(locatedPath);
        var rawVpnName = (entry.VpnName ?? string.Empty).Trim();
        var rawVpnServer = (entry.VpnServer ?? string.Empty).Trim();

        // 1. OpenVPN GUI: aciona perfil .ovpn direto (--command connect quando aberto na bandeja, --connect quando fechado)
        if (exeFileName.Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase))
        {
            var matchedOvpn = TryMatchOpenVpnProfileFile(locatedPath, entry);
            if (!string.IsNullOrWhiteSpace(matchedOvpn))
            {
                return BuildOpenVpnGuiProfileResolution(locatedPath, matchedOvpn, credential);
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

        // 2. OpenVPN Connect v3: suporta --list-profiles e --connect-shortcut=<profile_id>
        if (exeFileName.Equals("OpenVPNConnect.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(rawVpnName) &&
            _customProgramRoots is null)
        {
            var profileId = TryResolveOpenVpnConnectProfileId(locatedPath, rawVpnName);
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"OpenVPN Connect ({rawVpnName})",
                    TargetPathOrUri: locatedPath,
                    Arguments: [$"--connect-shortcut={profileId}"],
                    UseShellExecute: false,
                    IsWindowsNativeFallback: false,
                    HintMessage: L.Format("Conectando perfil '{0}' no OpenVPN Connect...", rawVpnName),
                    MatchedProfileName: rawVpnName,
                    AutomationVendor: credential is { HasAnyValue: true } ? VpnAutomationVendorKind.GenericGui : VpnAutomationVendorKind.None);
            }
        }

        // 3. FortiClient Console (7.x / 8.x): abre sempre o FortiClientConsole.exe com acessibilidade habilitada e automatiza via UIA
        if (exeFileName.Equals("FortiClientConsole.exe", StringComparison.OrdinalIgnoreCase) ||
            exeFileName.Equals("FortiClient.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Se existir FortiClientConsole.exe na mesma pasta que FortiClient.exe, prefere sempre FortiClientConsole.exe
            var targetForti = locatedPath;
            if (exeFileName.Equals("FortiClient.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(locatedPath);
                var consoleSibling = !string.IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "FortiClientConsole.exe") : null;
                if (!string.IsNullOrWhiteSpace(consoleSibling) && File.Exists(consoleSibling))
                    targetForti = consoleSibling;
            }

            return new VpnLaunchResolution(
                Found: true,
                DisplayClientName: "FortiClient",
                TargetPathOrUri: targetForti,
                Arguments: ["--force-renderer-accessibility"],
                UseShellExecute: false,
                IsWindowsNativeFallback: false,
                HintMessage: !string.IsNullOrWhiteSpace(rawVpnName)
                    ? L.Format("Abrindo FortiClient para o perfil '{0}'...", rawVpnName)
                    : L.Text("Abrindo console do FortiClient..."),
                MatchedProfileName: string.IsNullOrWhiteSpace(rawVpnName) ? null : rawVpnName,
                AutomationVendor: VpnAutomationVendorKind.FortiClient);
        }

        // 4. Utilitário CLI oficial FortiSSLVPNclient.exe (quando apontado explicitamente pelo analista)
        if (exeFileName.Equals("FortiSSLVPNclient.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(rawVpnName))
        {
            var args = new List<string> { "connect", "-s", rawVpnName };
            var passedCreds = false;
            if (credential is not null && !string.IsNullOrWhiteSpace(credential.FormattedUsername) && !string.IsNullOrEmpty(credential.Password))
            {
                args.Add("-u");
                args.Add($"{credential.FormattedUsername}:{credential.Password}");
                passedCreds = true;
            }

            return new VpnLaunchResolution(
                Found: true,
                DisplayClientName: $"FortiClient CLI ({rawVpnName})",
                TargetPathOrUri: locatedPath,
                Arguments: args,
                UseShellExecute: false,
                IsWindowsNativeFallback: false,
                HintMessage: L.Format("Conectando '{0}' via FortiSSLVPNclient...", rawVpnName),
                PassedCredentialsDirectly: passedCreds,
                MatchedProfileName: rawVpnName);
        }

        // 5. Cisco Secure Client / AnyConnect: quando há Usuário + Senha manuais e vpncli.exe disponível, autentica via pipe StandardInput (-s)
        if ((exeFileName.Equals("csc_ui.exe", StringComparison.OrdinalIgnoreCase) ||
             exeFileName.Equals("vpnui.exe", StringComparison.OrdinalIgnoreCase) ||
             exeFileName.Equals("vpncli.exe", StringComparison.OrdinalIgnoreCase)) &&
            credential is not null &&
            !string.IsNullOrWhiteSpace(credential.FormattedUsername) &&
            !string.IsNullOrEmpty(credential.Password))
        {
            var ciscoTarget = !string.IsNullOrWhiteSpace(rawVpnServer) ? rawVpnServer : rawVpnName;
            var vpnCliPath = TryFindCiscoVpnCli(locatedPath);
            if (!string.IsNullOrWhiteSpace(ciscoTarget) && !string.IsNullOrWhiteSpace(vpnCliPath))
            {
                var stdinPayload = $"connect {ciscoTarget}\n{credential.FormattedUsername}\n{credential.Password}\ny\n";
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"{displayName} ({ciscoTarget})",
                    TargetPathOrUri: vpnCliPath,
                    Arguments: ["-s"],
                    UseShellExecute: false,
                    IsWindowsNativeFallback: false,
                    HintMessage: L.Format("Conectando Cisco VPN em '{0}'...", ciscoTarget),
                    PassedCredentialsDirectly: true,
                    MatchedProfileName: ciscoTarget,
                    StandardInputPayload: stdinPayload);
            }
        }

        // 6. SonicWall NetExtender: quando há Servidor + Usuário + Senha manuais e NECLI.exe ao lado do NEGui.exe, conecta via CLI
        if (exeFileName.Equals("NEGui.exe", StringComparison.OrdinalIgnoreCase) &&
            credential is not null &&
            !string.IsNullOrWhiteSpace(credential.Username) &&
            !string.IsNullOrEmpty(credential.Password) &&
            !string.IsNullOrWhiteSpace(rawVpnServer))
        {
            var dir = Path.GetDirectoryName(locatedPath);
            var neCli = !string.IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "NECLI.exe") : null;
            if (!string.IsNullOrWhiteSpace(neCli) && File.Exists(neCli))
            {
                var cliArgs = new List<string>
                {
                    "connect",
                    "-s", rawVpnServer,
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
                    HintMessage: L.Format("Conectando SonicWall NetExtender em {0}...", rawVpnServer),
                    PassedCredentialsDirectly: true);
            }
        }

        var useShell = !locatedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var automationKind = vendor?.AutomationKind ?? VpnAutomationVendorKind.None;
        if (automationKind == VpnAutomationVendorKind.GenericGui && credential is not { HasAnyValue: true })
            automationKind = VpnAutomationVendorKind.None;

        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: displayName,
            TargetPathOrUri: locatedPath,
            Arguments: Array.Empty<string>(),
            UseShellExecute: useShell,
            IsWindowsNativeFallback: false,
            MatchedProfileName: string.IsNullOrWhiteSpace(rawVpnName) ? null : rawVpnName,
            AutomationVendor: automationKind);
    }

    private static string? TryFindCiscoVpnCli(string locatedCiscoPath)
    {
        if (Path.GetFileName(locatedCiscoPath).Equals("vpncli.exe", StringComparison.OrdinalIgnoreCase))
            return locatedCiscoPath;

        var dir = Path.GetDirectoryName(locatedCiscoPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        // Em Cisco AnyConnect: mesma pasta do vpnui.exe. Em Cisco Secure Client: pasta pai de UI\csc_ui.exe
        var directSibling = Path.Combine(dir, "vpncli.exe");
        if (File.Exists(directSibling))
            return directSibling;

        var parentDir = Directory.GetParent(dir)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            var parentCli = Path.Combine(parentDir, "vpncli.exe");
            if (File.Exists(parentCli))
                return parentCli;
        }

        return null;
    }

    private VpnLaunchResolution BuildOpenVpnExplicitFileResolution(string ovpnFilePath, VpnLoginCredential? credential)
    {
        var openVpnGuiExe = FindSpecificExecutableAmongVendors("openvpn-gui.exe");
        if (!string.IsNullOrWhiteSpace(openVpnGuiExe))
        {
            return BuildOpenVpnGuiProfileResolution(openVpnGuiExe, ovpnFilePath, credential);
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

    private VpnLaunchResolution BuildOpenVpnGuiProfileResolution(
        string openVpnGuiExePath,
        string ovpnFilePath,
        VpnLoginCredential? credential)
    {
        var baseName = Path.GetFileNameWithoutExtension(ovpnFilePath);
        var fileName = Path.GetFileName(ovpnFilePath);
        var alreadyRunning = IsProcessCurrentlyRunning("openvpn-gui");

        IReadOnlyList<string> args = alreadyRunning
            ? ["--command", "connect", baseName]
            : ["--connect", fileName];

        var hint = alreadyRunning
            ? L.Format("Conectando perfil '{0}' no OpenVPN GUI...", baseName)
            : L.Format("Iniciando OpenVPN GUI e conectando perfil '{0}'...", baseName);

        // Regra de não interferência: se não há usuário/senha manuais cadastrados no ConectaTI (ex.: Polividros.ovpn),
        // AutomationVendor permanece None e o OpenVPN GUI conecta sozinho com o login salvo nele.
        var automation = credential is { HasAnyValue: true }
            ? VpnAutomationVendorKind.OpenVpnGuiAuthDialog
            : VpnAutomationVendorKind.None;

        return new VpnLaunchResolution(
            Found: true,
            DisplayClientName: $"OpenVPN GUI ({baseName})",
            TargetPathOrUri: openVpnGuiExePath,
            Arguments: args,
            UseShellExecute: false,
            IsWindowsNativeFallback: false,
            HintMessage: hint,
            MatchedProfileName: baseName,
            AutomationVendor: automation);
    }

    private VpnLaunchOutcome ExecuteResolution(VpnLaunchResolution resolution)
    {
        try
        {
            // Se o processo gráfico já tiver janela aberta, minimizada ou oculta na bandeja (exceto openvpn-gui IPC), restaura primeiro
            if (_customStarter is null &&
                resolution.TargetPathOrUri.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(resolution.TargetPathOrUri).Equals("openvpn-gui.exe", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(resolution.StandardInputPayload))
            {
                var procName = Path.GetFileNameWithoutExtension(resolution.TargetPathOrUri);
                var restoredExisting = VpnWindowAutomator.TryRestoreProcessWindow([procName], [resolution.DisplayClientName, procName]);

                // Para apps que escondem janela única na bandeja (OpenConnect GUI, Radmin VPN), se já restauramos a janela existente,
                // não precisamos disparar segunda instância que apenas exibiria aviso de instância única
                if (restoredExisting &&
                    (procName.Equals("openconnect-gui", StringComparison.OrdinalIgnoreCase) ||
                     procName.Equals("RvRvpnGui", StringComparison.OrdinalIgnoreCase)))
                {
                    var restoredStatus = !string.IsNullOrWhiteSpace(resolution.HintMessage)
                        ? resolution.HintMessage!
                        : L.Format("Cliente VPN restaurado da bandeja: {0}", resolution.DisplayClientName);

                    _logger?.Info("VPN", $"Janela do cliente VPN restaurada da bandeja: {resolution.DisplayClientName}");
                    return new VpnLaunchOutcome(
                        Started: true,
                        UsedWindowsFallback: false,
                        ClientDisplayName: resolution.DisplayClientName,
                        StatusMessage: restoredStatus,
                        PassedCredentialsDirectly: resolution.PassedCredentialsDirectly,
                        MatchedProfileName: resolution.MatchedProfileName);
                }
            }

            var hasStdin = !string.IsNullOrEmpty(resolution.StandardInputPayload);
            var psi = new ProcessStartInfo
            {
                FileName = resolution.TargetPathOrUri,
                UseShellExecute = !hasStdin && resolution.UseShellExecute
            };

            if (!psi.UseShellExecute)
            {
                foreach (var arg in resolution.Arguments)
                    psi.ArgumentList.Add(arg);
                var dir = Path.GetDirectoryName(resolution.TargetPathOrUri);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    psi.WorkingDirectory = dir;

                if (hasStdin)
                {
                    psi.RedirectStandardInput = true;
                    psi.CreateNoWindow = true;
                }
            }

            if (_customStarter is not null)
            {
                if (hasStdin)
                    _customStdinObserver?.Invoke(resolution.StandardInputPayload!);

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
                var proc = Process.Start(psi);
                if (proc is not null && hasStdin)
                {
                    try
                    {
                        proc.StandardInput.Write(resolution.StandardInputPayload);
                        proc.StandardInput.Flush();
                        proc.StandardInput.Close();
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
                else
                {
                    proc?.Dispose();
                }
            }

            var status = !string.IsNullOrWhiteSpace(resolution.HintMessage)
                ? resolution.HintMessage!
                : L.Format("Cliente VPN aberto: {0}", resolution.DisplayClientName);

            // Nunca registra argumentos nem payloads stdin no log para preservar segredos
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
                !string.IsNullOrWhiteSpace(credential.FormattedUsername) &&
                !string.IsNullOrEmpty(credential.Password))
            {
                return new VpnLaunchResolution(
                    Found: true,
                    DisplayClientName: $"VPN do Windows ({cleanName})",
                    TargetPathOrUri: rasdial,
                    Arguments: [cleanName, credential.FormattedUsername, credential.Password],
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

            var exact = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(withoutExt, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return exact;

            var normTarget = CompactNormalize(withoutExt);
            if (!string.IsNullOrEmpty(normTarget))
            {
                var compactExact = files.FirstOrDefault(f =>
                    CompactNormalize(Path.GetFileNameWithoutExtension(f)).Equals(normTarget, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(compactExact))
                    return compactExact;
            }

            var stemMatch = TryMatchUniqueByStem(files, rawVpnName);
            if (!string.IsNullOrWhiteSpace(stemMatch))
                return stemMatch;
        }

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
        // Cobre tanto SSL-VPN quanto IPsec, em HKCU ("VPNs Pessoais" do FortiClient 7.x) e HKLM (túneis globais)
        var subKeys = new[]
        {
            @"Software\Fortinet\FortiClient\Sslvpn\Tunnels",
            @"Software\Fortinet\FortiClient\IPSec\Tunnels",
            @"SOFTWARE\WOW6432Node\Fortinet\FortiClient\Sslvpn\Tunnels",
            @"SOFTWARE\WOW6432Node\Fortinet\FortiClient\IPSec\Tunnels"
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

    private static void DiscoverCiscoXmlProfiles(Action<DiscoveredVpnProfile> addProfile)
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonAppData))
            return;

        var dirs = new[]
        {
            Path.Combine(commonAppData, @"Cisco\Cisco Secure Client\VPN\Profile"),
            Path.Combine(commonAppData, @"Cisco\Cisco AnyConnect Secure Mobility Client\Profile")
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var xmlFile in EnumerateFilesBounded(dir, "*.xml", maxDepth: 1))
            {
                try
                {
                    var doc = XDocument.Load(xmlFile);
                    foreach (var hostEntry in doc.Descendants().Where(e => e.Name.LocalName == "HostEntry"))
                    {
                        var hostName = hostEntry.Elements().FirstOrDefault(e => e.Name.LocalName == "HostName")?.Value?.Trim();
                        var hostAddr = hostEntry.Elements().FirstOrDefault(e => e.Name.LocalName == "HostAddress")?.Value?.Trim();
                        var profileName = !string.IsNullOrWhiteSpace(hostName) ? hostName : hostAddr;
                        if (!string.IsNullOrWhiteSpace(profileName))
                            addProfile(new DiscoveredVpnProfile(profileName, "Cisco Secure Client (AnyConnect)", hostAddr, xmlFile));
                    }
                }
                catch { }
            }
        }
    }

    private void DiscoverOpenVpnConnectProfiles(Action<DiscoveredVpnProfile> addProfile)
    {
        var vendor = KnownVendors.FirstOrDefault(v => v.DisplayName == "OpenVPN Connect");
        var exe = vendor is not null ? TryLocateVendorExecutableOrShortcut(vendor) : null;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var (id, name, host) in QueryOpenVpnConnectProfiles(exe))
        {
            var label = !string.IsNullOrWhiteSpace(name) ? name : id;
            addProfile(new DiscoveredVpnProfile(label, "OpenVPN Connect", host, id));
        }
    }

    private static string? TryResolveOpenVpnConnectProfileId(string openVpnConnectExe, string targetName)
    {
        var profiles = QueryOpenVpnConnectProfiles(openVpnConnectExe);
        if (profiles.Count == 0)
            return null;

        var normTarget = CompactNormalize(targetName);
        foreach (var (id, name, host) in profiles)
        {
            if (id.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                CompactNormalize(name).Equals(normTarget, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(host) && host.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }

        return null;
    }

    private static IReadOnlyList<(string Id, string Name, string? Host)> QueryOpenVpnConnectProfiles(string openVpnConnectExe)
    {
        var list = new List<(string, string, string?)>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = openVpnConnectExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--list-profiles");

            using var proc = Process.Start(psi);
            if (proc is null)
                return list;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            if (string.IsNullOrWhiteSpace(output))
                return list;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var host = item.TryGetProperty("host", out var hostProp) ? hostProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id))
                        list.Add((id!, name ?? id!, host));
                }
            }
        }
        catch { }

        return list;
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
        // 1. Verifica primeiro os caminhos padrão de instalação (garante pegar sempre o Console/GUI real e nunca um daemon de bandeja)
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

        // 2. Se estiver em pasta customizada fora de Program Files, consulta somente os processos gráficos reais (WindowProcessNames)
        if (_customProgramRoots is null)
        {
            foreach (var procName in vendor.WindowProcessNames)
            {
                var runningPath = TryGetRunningProcessPath(procName);
                if (!string.IsNullOrWhiteSpace(runningPath) && File.Exists(runningPath))
                    return runningPath;
            }

            foreach (var rel in vendor.RelativeCandidatePaths)
            {
                var exeName = Path.GetFileName(rel.Replace('\\', Path.DirectorySeparatorChar));
                var fromRegOrPath = TryFindInAppPathsOrSystemPath(exeName);
                if (!string.IsNullOrWhiteSpace(fromRegOrPath))
                    return fromRegOrPath;
            }
        }

        // 3. Atalhos (.lnk) no Menu Iniciar
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
}
