namespace ConectaTI.App.Models;

public class AppSettings
{
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

    // Segurança, Bloqueio e Área de Transferência
    public int AutoLockIdleMinutes { get; set; } = 10;
    public int ClipboardClearSeconds { get; set; } = 30;
    public int PasswordRevealSeconds { get; set; } = 15;

    // Senha mestra opcional (protege chaves de sessão e desbloqueio)
    public bool HasMasterPassword { get; set; }
    public string MasterPasswordSaltBase64 { get; set; } = string.Empty;
    public string MasterPasswordVerifierBase64 { get; set; } = string.Empty;

    // Geometria da janela e painéis
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 620;
    public double WindowHeight { get; set; } = 500;
    public bool IsMaximized { get; set; }
    public double LeftPaneWidth { get; set; } = 220;
    public double RightPaneWidth { get; set; } = 395;
}
