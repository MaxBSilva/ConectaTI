namespace ConectaTI.App.Models;

public class AppSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.Automatic;
    public bool ComfortableMode { get; set; }
    public double FontSize { get; set; } = 12.5;
    public Dictionary<string, double> ImportColumnWidths { get; set; } = new();
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    // Caminhos dos clientes externos
    public string MstscExecutablePath { get; set; } = string.Empty;
    public string PuttyExecutablePath { get; set; } = string.Empty;
    public string OpenSshExecutablePath { get; set; } = string.Empty;
    public string WinScpExecutablePath { get; set; } = string.Empty;
    public string VncExecutablePath { get; set; } = string.Empty;

    public SshClientPreference PreferredSshClient { get; set; } = SshClientPreference.Putty;
    public VncViewerFlavor VncFlavor { get; set; } = VncViewerFlavor.TigerRealVnc;
    public string VncCustomArgumentsTemplate { get; set; } = "{host}:{port}";

    // Integração opcional do RDP com o Gerenciador de Credenciais do Windows (TERMSRV/host)
    public bool RegisterRdpCredentialsInWindowsVault { get; set; } = true;

    // Verifica conectividade rápida e sugere abrir a VPN quando o servidor não responde
    public bool CheckVpnBeforeConnect { get; set; } = true;

    // Segurança, Bloqueio e Área de Transferência
    public int AutoLockIdleMinutes { get; set; } = 10;
    public int ClipboardClearSeconds { get; set; } = 30;
    public int PasswordRevealSeconds { get; set; } = 15;

    // Senha mestra opcional (protege chaves de sessão e desbloqueio)
    public bool HasMasterPassword { get; set; }
    public string MasterPasswordSaltBase64 { get; set; } = string.Empty;
    public string MasterPasswordVerifierBase64 { get; set; } = string.Empty;

    // Inicialização com o Windows e até 3 sites automáticos no navegador padrão
    public bool OpenStartupWebsites { get; set; } = true;
    public string StartupWebsite1 { get; set; } = string.Empty;
    public string StartupWebsite2 { get; set; } = string.Empty;
    public string StartupWebsite3 { get; set; } = string.Empty;

    // Atalhos personalizados da janela principal (chave = ActionId, valor = combinação ex.: "Ctrl+Shift+N")
    public Dictionary<string, string> CustomShortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Geometria da janela e painéis
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 620;
    public double WindowHeight { get; set; } = 500;
    public bool IsMaximized { get; set; }
    public double LeftPaneWidth { get; set; } = 220;
    public double RightPaneWidth { get; set; } = 395;
}
