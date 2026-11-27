using System.Text;
using ConectaTI.App.Models;
using ConectaTI.App.Helpers;
namespace ConectaTI.App.Services;

public static class ConnectionCopyFormatter
{
    public static string Format(ConnectionEntry entry, string host, string user, string? password, string? vpnPassword = null)
    {
        var text = new StringBuilder();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) text.AppendLine($"{L.Text(label)}: {value}"); }
        string Yes(bool value) => L.Text(value ? "Sim" : "Não");
        Add("Nome da conexão", entry.Name); Add("Status", entry.ActivityDisplay); Add("Tipo de conexão", entry.ProtocolDisplay);
        Add("Endereço", host);
        if (!string.IsNullOrWhiteSpace(host))
        {
            var parsed = HostPortParser.Parse(host, entry.Protocol);
            var port = parsed.HasExplicitPort ? parsed.Port : entry.Port;
            if (port > 0) Add("Porta", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Add("Usuário", user); Add("Senha", password);
        Add("Cliente", entry.ClientName); Add("Grupo", entry.GroupName); if (entry.Environment != EnvironmentType.Unspecified) Add("Ambiente", entry.EnvironmentDisplay); Add("Observações", entry.Notes);
        if (entry.Protocol == ConnectionProtocol.Rdp)
        {
            Add("Modo administrativo", Yes(entry.RdpAdminSession)); Add("Tela inteira", Yes(entry.RdpFullScreen));
            Add("Múltiplos monitores", Yes(entry.RdpUseMultimon));
            if (!entry.RdpFullScreen && !entry.RdpUseMultimon) Add("Resolução", $"{entry.RdpWidth} × {entry.RdpHeight}");
            var gateway = entry.RdpOptions;
            if (!string.IsNullOrWhiteSpace(gateway.GatewayHostname) && gateway.GatewayUsageMethod is not (0 or 4))
            {
                text.AppendLine().AppendLine(L.Text("Gateway RDP")); Add("Servidor", gateway.GatewayHostname);
                Add("Modo de utilização", Describe(gateway.GatewayUsageMethod, "gateway"));
                Add("Origem das credenciais", Describe(gateway.GatewayCredentialsSource, "credentials"));
                Add("Perfil do gateway", Describe(gateway.GatewayProfileUsageMethod, "profile"));
                Add("Usar as mesmas credenciais no destino", Yes(gateway.PromptCredentialOnce));
            }
        }
        if (entry.HasVpnConfigured)
        {
            text.AppendLine().AppendLine(L.Text("VPN")); Add("Nome da VPN", entry.VpnName); Add("Aplicativo VPN", entry.VpnApp);
            Add("Servidor VPN", entry.VpnServer); Add("Usuário VPN", entry.VpnUsername); Add("Senha VPN", vpnPassword); Add("Observações VPN", entry.VpnNotes);
        }
        return text.ToString().TrimEnd();
    }
    public static string Describe(int value, string kind)
    {
        var description = (kind, value) switch
        {
            ("gateway", 0) => "Não utilizar gateway", ("gateway", 1) => "Utilizar sempre",
            ("gateway", 2) => "Utilizar se a conexão direta falhar", ("gateway", 3) => "Configurações padrão",
            ("gateway", 4) => "Não utilizar gateway; detectar endereços locais",
            ("credentials", 0) => "Senha", ("credentials", 1) => "Cartão inteligente",
            ("credentials", 2) => "Credenciais do usuário conectado", ("credentials", 3) => "Autenticação básica",
            ("credentials", 5) => "Autenticação por cookie", ("credentials", 4) => "Permitir escolha posterior",
            ("profile", 0) => "Perfil padrão", ("profile", 1) => "Configurações explícitas",
            _ => "Valor configurado"
        };
        return $"{L.Text(description)} ({value.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }
}


