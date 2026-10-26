using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using ConectaTI.App.Helpers;

namespace ConectaTI.App.Models;

public class ConnectionEntry : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    private string _clientName = string.Empty;
    public string ClientName
    {
        get => _clientName;
        set => SetProperty(ref _clientName, value);
    }
    private string _groupName = "Geral";
    public string GroupName
    {
        get => _groupName;
        set => SetProperty(ref _groupName, value);
    }
    private EnvironmentType _environment = EnvironmentType.Production;
    public EnvironmentType Environment
    {
        get => _environment;
        set => SetProperty(ref _environment, value);
    }
    private ConnectionProtocol _protocol = ConnectionProtocol.Rdp;
    public ConnectionProtocol Protocol
    {
        get => _protocol;
        set => SetProperty(ref _protocol, value);
    }
    private string _host = string.Empty;
    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }
    private int _port = 3389;
    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }
    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }
    private string _domain = string.Empty;
    public string Domain
    {
        get => _domain;
        set => SetProperty(ref _domain, value);
    }
    private string? _credentialId;
    public string? CredentialId
    {
        get => _credentialId;
        set => SetProperty(ref _credentialId, value);
    }
    private string _privateKeyPath = string.Empty;
    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => SetProperty(ref _privateKeyPath, value);
    }
    private string _notes = string.Empty;
    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }
    private string _tags = string.Empty;
    public string Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value);
    }
    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    // Configurações específicas de RDP
    private bool _rdpFullScreen = true;
    public bool RdpFullScreen
    {
        get => _rdpFullScreen;
        set => SetProperty(ref _rdpFullScreen, value);
    }
    private int _rdpWidth = 1920;
    public int RdpWidth
    {
        get => _rdpWidth;
        set => SetProperty(ref _rdpWidth, value);
    }
    private int _rdpHeight = 1080;
    public int RdpHeight
    {
        get => _rdpHeight;
        set => SetProperty(ref _rdpHeight, value);
    }
    private bool _rdpUseMultimon;
    public bool RdpUseMultimon
    {
        get => _rdpUseMultimon;
        set => SetProperty(ref _rdpUseMultimon, value);
    }
    private bool _rdpAdminSession;
    public bool RdpAdminSession
    {
        get => _rdpAdminSession;
        set => SetProperty(ref _rdpAdminSession, value);
    }

    // Configurações específicas de SSH
    private SshClientPreference _sshClient = SshClientPreference.Default;
    public SshClientPreference SshClient
    {
        get => _sshClient;
        set => SetProperty(ref _sshClient, value);
    }
    private bool _sshUseAgent = true;
    public bool SshUseAgent
    {
        get => _sshUseAgent;
        set => SetProperty(ref _sshUseAgent, value);
    }

    // Configurações específicas de VNC
    private string _vncParameters = string.Empty;
    public string VncParameters
    {
        get => _vncParameters;
        set => SetProperty(ref _vncParameters, value);
    }

    // Configurações opcionais de VPN do cliente
    private string _vpnName = string.Empty;
    public string VpnName
    {
        get => _vpnName;
        set
        {
            if (SetProperty(ref _vpnName, value))
            {
                OnPropertyChanged(nameof(HasVpnConfigured));
                OnPropertyChanged(nameof(VpnStatusBadge));
            }
        }
    }

    private string _vpnApp = string.Empty;
    public string VpnApp
    {
        get => _vpnApp;
        set
        {
            if (SetProperty(ref _vpnApp, value))
            {
                OnPropertyChanged(nameof(HasVpnConfigured));
                OnPropertyChanged(nameof(VpnStatusBadge));
            }
        }
    }

    private string _vpnServer = string.Empty;
    public string VpnServer
    {
        get => _vpnServer;
        set
        {
            if (SetProperty(ref _vpnServer, value))
            {
                OnPropertyChanged(nameof(HasVpnConfigured));
                OnPropertyChanged(nameof(VpnStatusBadge));
            }
        }
    }

    private string _vpnNotes = string.Empty;
    public string VpnNotes
    {
        get => _vpnNotes;
        set
        {
            if (SetProperty(ref _vpnNotes, value))
            {
                OnPropertyChanged(nameof(HasVpnConfigured));
                OnPropertyChanged(nameof(VpnStatusBadge));
            }
        }
    }

    [JsonIgnore]
    public bool HasVpnConfigured =>
        !string.IsNullOrWhiteSpace(VpnName) ||
        !string.IsNullOrWhiteSpace(VpnApp) ||
        !string.IsNullOrWhiteSpace(VpnServer) ||
        !string.IsNullOrWhiteSpace(VpnNotes);

    [JsonIgnore]
    public string VpnStatusBadge => HasVpnConfigured ? L.Text("● Configurada") : string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpenedAtUtc { get; set; }

    // Soft delete (Lixeira)
    private bool _isDeleted;
    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetProperty(ref _isDeleted, value);
    }
    public DateTime? DeletedAtUtc { get; set; }

    // Propriedades calculadas para a interface
    [JsonIgnore]
    public string EnvironmentDisplay => Environment.ToDisplayName();

    [JsonIgnore]
    public string EnvironmentBadge => Environment.ToBadgeText();

    [JsonIgnore]
    public bool HasEnvironmentBadge => !string.IsNullOrEmpty(EnvironmentBadge);

    [JsonIgnore]
    public string DeletedAtDisplay => DeletedAtUtc.HasValue
        ? DeletedAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : string.Empty;

    [JsonIgnore]
    public string ProtocolDisplay => Protocol.ToDisplayName();

    [JsonIgnore]
    public string ProtocolShortDisplay => Protocol.ToShortDisplayName();

    [JsonIgnore]
    public string EndpointDisplay => ConnectionValidator.FormatHostAndPort(Host, Port);

    [JsonIgnore]
    public string RawEffectiveUser => UserIdentityFormatter.FormatDisplay(Username, Domain);

    [JsonIgnore]
    public bool HasEffectiveUser => !string.IsNullOrWhiteSpace(RawEffectiveUser);

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [JsonIgnore]
    public string EffectiveUserDisplay
    {
        get
        {
            var formatted = RawEffectiveUser;
            return string.IsNullOrWhiteSpace(formatted) ? L.Text("Não informado") : formatted;
        }
    }

    [JsonIgnore]
    public string LastOpenedDisplay => LastOpenedAtUtc.HasValue
        ? LastOpenedAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : L.Text("Nunca acessado");

    [JsonIgnore]
    public IReadOnlyList<string> TagList => string.IsNullOrWhiteSpace(Tags)
        ? Array.Empty<string>()
        : Tags.Split(',', ';')
              .Select(t => t.Trim())
              .Where(t => !string.IsNullOrEmpty(t))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    public void RefreshDisplayProperties()
    {
        foreach (var name in new[] { nameof(EnvironmentDisplay), nameof(EnvironmentBadge), nameof(ProtocolDisplay), nameof(EffectiveUserDisplay), nameof(LastOpenedDisplay), nameof(VpnStatusBadge) }) OnPropertyChanged(name);
    }

    public ConnectionEntry Clone()
    {
        return new ConnectionEntry
        {
            Id = Id,
            Name = Name,
            ClientName = ClientName,
            GroupName = GroupName,
            Environment = Environment,
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Domain = Domain,
            CredentialId = CredentialId,
            PrivateKeyPath = PrivateKeyPath,
            Notes = Notes,
            Tags = Tags,
            IsFavorite = IsFavorite,
            RdpFullScreen = RdpFullScreen,
            RdpWidth = RdpWidth,
            RdpHeight = RdpHeight,
            RdpUseMultimon = RdpUseMultimon,
            RdpAdminSession = RdpAdminSession,
            SshClient = SshClient,
            SshUseAgent = SshUseAgent,
            VncParameters = VncParameters,
            VpnName = VpnName,
            VpnApp = VpnApp,
            VpnServer = VpnServer,
            VpnNotes = VpnNotes,
            IsDeleted = IsDeleted,
            DeletedAtUtc = DeletedAtUtc,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            LastOpenedAtUtc = LastOpenedAtUtc
        };
    }

    public ConnectionEntry CreateDuplicate()
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid().ToString("D");
        copy.Name = string.IsNullOrWhiteSpace(Name) ? L.Text("Nova Conexão (Cópia)") : L.Format("{0} (Cópia)", Name);
        copy.IsDeleted = false;
        copy.DeletedAtUtc = null;
        copy.CreatedAtUtc = DateTime.UtcNow;
        copy.UpdatedAtUtc = DateTime.UtcNow;
        copy.LastOpenedAtUtc = null;
        return copy;
    }

    public bool HasSameEditableValues(ConnectionEntry other)
    {
        return string.Equals(Name, other.Name, StringComparison.Ordinal) &&
               string.Equals(ClientName, other.ClientName, StringComparison.Ordinal) &&
               string.Equals(GroupName, other.GroupName, StringComparison.Ordinal) &&
               Environment == other.Environment &&
               Protocol == other.Protocol &&
               string.Equals(Host, other.Host, StringComparison.Ordinal) &&
               Port == other.Port &&
               string.Equals(Username, other.Username, StringComparison.Ordinal) &&
               string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
               string.Equals(CredentialId ?? string.Empty, other.CredentialId ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(PrivateKeyPath, other.PrivateKeyPath, StringComparison.Ordinal) &&
               string.Equals(Notes, other.Notes, StringComparison.Ordinal) &&
               string.Equals(Tags, other.Tags, StringComparison.Ordinal) &&
               IsFavorite == other.IsFavorite &&
               RdpFullScreen == other.RdpFullScreen &&
               RdpWidth == other.RdpWidth &&
               RdpHeight == other.RdpHeight &&
               RdpUseMultimon == other.RdpUseMultimon &&
               RdpAdminSession == other.RdpAdminSession &&
               SshClient == other.SshClient &&
               SshUseAgent == other.SshUseAgent &&
               string.Equals(VncParameters, other.VncParameters, StringComparison.Ordinal) &&
               string.Equals(VpnName, other.VpnName, StringComparison.Ordinal) &&
               string.Equals(VpnApp, other.VpnApp, StringComparison.Ordinal) &&
               string.Equals(VpnServer, other.VpnServer, StringComparison.Ordinal) &&
               string.Equals(VpnNotes, other.VpnNotes, StringComparison.Ordinal);
    }
}
