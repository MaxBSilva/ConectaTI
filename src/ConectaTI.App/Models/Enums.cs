namespace ConectaTI.App.Models;

public enum AppLanguage { Automatic, Portuguese, English, Spanish }
public enum InterfaceDensity { Compact, Standard, Comfortable }

public sealed class SelectableOption<T> : System.ComponentModel.INotifyPropertyChanged, ILanguageObserver
{
    public T Value { get; }
    private readonly string _label;
    public string DisplayName => L.Text(_label);
    public SelectableOption(T value, string displayName) { Value = value; _label = displayName; LocalizationService.Current.Observe(this); }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void OnLanguageChanged() => PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
    public override string ToString() => DisplayName;
}

public enum EnvironmentType
{
    Production = 0,
    Staging = 1,
    Testing = 2,
    Other = 3,
    Unspecified = 4
}

public enum ConnectionViewMode
{
    All = 0,
    Favorites = 1,
    Recent = 2,
    Trash = 3
}

public enum ConnectionProtocol
{
    Rdp = 0,
    Ssh = 1,
    Sftp = 2,
    Scp = 3,
    Vnc = 4
}

public enum ConnectionTypeOption
{
    Mstsc = 0,
    SshPutty = 1,
    SshOpenSsh = 2,
    Vnc = 3,
    WinScpSftp = 4,
    WinScpScp = 5
}

public enum SshClientPreference
{
    Default = 0,
    Putty = 1,
    OpenSsh = 2
}

public enum VncViewerFlavor
{
    TigerRealVnc = 0,
    TightVnc = 1,
    UltraVnc = 2,
    Custom = 3
}

public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum ConnectionSortOption
{
    NameAsc = 0,
    ClientAsc = 1,
    LastOpenedDesc = 2
}

public enum DuplicateImportHandling
{
    Ignore = 0,
    Update = 1,
    CreateNew = 2
}

public enum UnsavedChangesChoice
{
    Save = 0,
    Discard = 1,
    Cancel = 2
}

public static class EnumDisplayExtensions
{
    public static string ToDisplayName(this EnvironmentType env) => env switch
    {
        EnvironmentType.Production => L.Text("Produção"),
        EnvironmentType.Staging => L.Text("Homologação"),
        EnvironmentType.Testing => L.Text("Teste"),
        EnvironmentType.Unspecified or EnvironmentType.Other => L.Text("Não informado"),
        _ => L.Text("Não informado")
    };

    public static string ToInvariantDisplayName(this EnvironmentType env) => env switch
    {
        EnvironmentType.Production => "Produção",
        EnvironmentType.Staging => "Homologação",
        EnvironmentType.Testing => "Teste",
        EnvironmentType.Unspecified or EnvironmentType.Other => "Não informado",
        _ => "Não informado"
    };

    public static string ToBadgeText(this EnvironmentType env) => env switch
    {
        EnvironmentType.Production => "PROD",
        EnvironmentType.Staging => L.Text("HML"),
        EnvironmentType.Testing => L.Text("TESTE"),
        _ => string.Empty
    };

    public static EnvironmentType ParseEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EnvironmentType.Unspecified;
        var norm = value.Trim().ToLowerInvariant();
        return norm switch
        {
            "producao" or "produção" or "prod" or "production" or "producción" or "prd" or "0" => EnvironmentType.Production,
            "homologacao" or "homologação" or "homolog" or "staging" or "homologación" or "hml" or "qa" or "1" => EnvironmentType.Staging,
            "teste" or "test" or "testing" or "prueba" or "dev" or "desenvolvimento" or "tst" or "2" => EnvironmentType.Testing,
            "outro" or "other" or "3" => EnvironmentType.Other,
            "nao informado" or "não informado" or "nenhum" or "none" or "unspecified" or "no especificado" or "not specified" or "4" => EnvironmentType.Unspecified,
            _ => EnvironmentType.Unspecified
        };
    }

    public static string ToDisplayName(this ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => L.Text("Área de Trabalho Remota (MSTSC)"),
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Sftp => "SFTP (WinSCP)",
        ConnectionProtocol.Scp => "SCP (WinSCP)",
        ConnectionProtocol.Vnc => "VNC",
        _ => protocol.ToString().ToUpperInvariant()
    };

    public static string ToInvariantDisplayName(this ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "Área de Trabalho Remota (MSTSC)",
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Sftp => "SFTP (WinSCP)",
        ConnectionProtocol.Scp => "SCP (WinSCP)",
        ConnectionProtocol.Vnc => "VNC",
        _ => protocol.ToString().ToUpperInvariant()
    };

    public static string ToShortDisplayName(this ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "RDP",
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.Scp => "SCP",
        ConnectionProtocol.Vnc => "VNC",
        _ => "RDP"
    };

    public static string ToDisplayName(this SshClientPreference pref) => pref switch
    {
        SshClientPreference.Default => L.Text("Padrão configurado"),
        SshClientPreference.Putty => "PuTTY",
        SshClientPreference.OpenSsh => L.Text("OpenSSH do Windows"),
        _ => L.Text("Padrão configurado")
    };

    public static int GetDefaultPort(this ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => 3389,
        ConnectionProtocol.Ssh => 22,
        ConnectionProtocol.Sftp => 22,
        ConnectionProtocol.Scp => 22,
        ConnectionProtocol.Vnc => 5900,
        _ => 3389
    };

    public static string ToDisplayName(this ConnectionTypeOption option) => option switch
    {
        ConnectionTypeOption.Mstsc => L.Text("Área de Trabalho Remota (MSTSC)"),
        ConnectionTypeOption.SshPutty => "SSH — PuTTY",
        ConnectionTypeOption.SshOpenSsh => "SSH — OpenSSH",
        ConnectionTypeOption.Vnc => "VNC",
        ConnectionTypeOption.WinScpSftp => "WinSCP — SFTP",
        ConnectionTypeOption.WinScpScp => "WinSCP — SCP",
        _ => L.Text("Área de Trabalho Remota (MSTSC)")
    };

    public static int GetDefaultPort(this ConnectionTypeOption option) => option switch
    {
        ConnectionTypeOption.Mstsc => 3389,
        ConnectionTypeOption.SshPutty => 22,
        ConnectionTypeOption.SshOpenSsh => 22,
        ConnectionTypeOption.Vnc => 5900,
        ConnectionTypeOption.WinScpSftp => 22,
        ConnectionTypeOption.WinScpScp => 22,
        _ => 3389
    };

    public static ConnectionProtocol ToProtocol(this ConnectionTypeOption option) => option switch
    {
        ConnectionTypeOption.Mstsc => ConnectionProtocol.Rdp,
        ConnectionTypeOption.SshPutty => ConnectionProtocol.Ssh,
        ConnectionTypeOption.SshOpenSsh => ConnectionProtocol.Ssh,
        ConnectionTypeOption.Vnc => ConnectionProtocol.Vnc,
        ConnectionTypeOption.WinScpSftp => ConnectionProtocol.Sftp,
        ConnectionTypeOption.WinScpScp => ConnectionProtocol.Scp,
        _ => ConnectionProtocol.Rdp
    };

    public static SshClientPreference ToSshClientPreference(this ConnectionTypeOption option) => option switch
    {
        ConnectionTypeOption.SshOpenSsh => SshClientPreference.OpenSsh,
        ConnectionTypeOption.SshPutty => SshClientPreference.Putty,
        _ => SshClientPreference.Default
    };

    public static ConnectionTypeOption FromProtocolAndSsh(ConnectionProtocol protocol, SshClientPreference sshPref) => protocol switch
    {
        ConnectionProtocol.Rdp => ConnectionTypeOption.Mstsc,
        ConnectionProtocol.Ssh => sshPref == SshClientPreference.OpenSsh
            ? ConnectionTypeOption.SshOpenSsh
            : ConnectionTypeOption.SshPutty,
        ConnectionProtocol.Vnc => ConnectionTypeOption.Vnc,
        ConnectionProtocol.Sftp => ConnectionTypeOption.WinScpSftp,
        ConnectionProtocol.Scp => ConnectionTypeOption.WinScpScp,
        _ => ConnectionTypeOption.Mstsc
    };

    public static ConnectionProtocol ParseProtocol(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ConnectionProtocol.Rdp;
        var norm = value.Trim().ToLowerInvariant();
        if (norm.Contains("mstsc") || norm.Contains("remota") || norm.Contains("remote desktop"))
            return ConnectionProtocol.Rdp;

        return norm switch
        {
            "rdp" or "ts" or "terminal" or "0" => ConnectionProtocol.Rdp,
            "ssh" or "putty" or "openssh" or "1" => ConnectionProtocol.Ssh,
            "sftp" or "sftp (winscp)" or "winscp" or "winscp-sftp" or "2" => ConnectionProtocol.Sftp,
            "scp" or "scp (winscp)" or "winscp-scp" or "3" => ConnectionProtocol.Scp,
            "vnc" or "tightvnc" or "tigervnc" or "realvnc" or "ultravnc" or "4" => ConnectionProtocol.Vnc,
            _ => ConnectionProtocol.Rdp
        };
    }
}
