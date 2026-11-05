using System.Xml.Linq;
using ConectaTI.App.Models;
using static ConectaTI.App.Services.ImportExport.ConnectionTransferValues;
namespace ConectaTI.App.Services.ImportExport;

// Native ConectaTI XML. Legacy names and attribute/element lookup remain compatible.
internal static class ConnectionXmlCodec
{
    public static XDocument Write(IEnumerable<ConnectionEntry> connections)
    {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("ConectaTI",
                    new XAttribute("Version", "1.0"),
                    new XAttribute("ExportedAtUtc", DateTime.UtcNow.ToString("o")),
                    new XElement("Connections",
                        connections.Select(c => new XElement("Connection",
                            new XElement("Id", c.Id),
                            new XElement("Name", c.Name),
                            new XElement("Client", c.ClientName),
                            new XElement("Group", c.GroupName),
                            new XElement("Environment", c.Environment.ToString()),
                            new XElement("Protocol", c.Protocol.ToString()),
                            new XElement("SshClient", c.SshClient.ToString()),
                            new XElement("SshUseAgent", c.SshUseAgent),
                            new XElement("VncParameters", c.VncParameters),
                            new XElement("Host", c.Host),
                            new XElement("Port", c.Port),
                            new XElement("Username", c.Username),
                            new XElement("Domain", c.Domain),
                            new XElement("PrivateKeyPath", c.PrivateKeyPath),
                            new XElement("VpnName", c.VpnName),
                            new XElement("VpnApp", c.VpnApp),
                            new XElement("VpnServer", c.VpnServer),
                            new XElement("VpnNotes", c.VpnNotes),
                            new XElement("Tags", c.Tags),
                            new XElement("Notes", c.Notes),
                            new XElement("RdpConfiguration", RdpFileService.ExportMetadata(c)),
                            new XElement("IsActive", c.IsActive),
                            new XElement("IsFavorite", c.IsFavorite)
                        ))
                    )
                )
            );


        return doc;
    }
    public static List<ConnectionEntry> Read(string text)
    {
                var doc = ImportLimits.ReadXml(text);
                var candidates = new List<ConnectionEntry>();
                foreach (var node in doc.Descendants().Where(e => e.Name.LocalName.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
                {
                    AutoPuttyCodec.CheckReadProgress(candidates.Count + 1);
                    var name = GetValue(node, "Name");
                    var client = GetValue(node, "Client", "ClientName");
                    var group = GetValue(node, "Group", "GroupName");
                    var protoStr = GetValue(node, "Protocol");
                    var proto = EnumDisplayExtensions.ParseProtocol(protoStr);
                    var sshPrefStr = GetValue(node, "SshClient", "SshPreference");
                    var sshPref = ParseSshClient(sshPrefStr);
                    var host = GetValue(node, "Host");
                    var portStr = GetValue(node, "Port");
                    var port = int.TryParse(portStr, out var p) ? p : proto.GetDefaultPort();
                    var user = GetValue(node, "Username", "User");
                    var domain = GetValue(node, "Domain");
                    var keyPath = GetValue(node, "PrivateKeyPath", "PrivateKey");
                    var vpnName = GetValue(node, "VpnName", "VPNName", "Vpn");
                    var vpnApp = GetValue(node, "VpnApp", "VPNApp", "VpnApplication", "AplicativoVpn");
                    var vpnServer = GetValue(node, "VpnServer", "VPNServer", "VpnHost", "ServidorVpn");
                    var vpnNotes = GetValue(node, "VpnNotes", "VPNNotes", "ObservacoesVpn");
                    var tags = GetValue(node, "Tags");
                    var notes = GetValue(node, "Notes");
                    var favStr = GetValue(node, "IsFavorite");
                    var isFav = bool.TryParse(favStr, out var fav) && fav;
                    var envStr = GetValue(node, "Environment");
                    var env = EnumDisplayExtensions.ParseEnvironment(envStr);

                    candidates.Add(new ConnectionEntry
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Name = string.IsNullOrWhiteSpace(name) ? host : name,
                        ClientName = string.IsNullOrWhiteSpace(client) ? "Geral" : client,
                        GroupName = string.IsNullOrWhiteSpace(group) ? "Geral" : group,
                        Environment = env,
                        Protocol = proto,
                        SshClient = sshPref,
                        SshUseAgent = ParseAgent(GetValue(node, "SshUseAgent")),
                        VncParameters = GetValue(node, "VncParameters"),
                        Host = host,
                        Port = port,
                        Username = user,
                        Domain = domain,
                        PrivateKeyPath = keyPath,
                        VpnName = vpnName,
                        VpnApp = vpnApp,
                        VpnServer = vpnServer,
                        VpnNotes = vpnNotes,
                        Tags = tags,
                        Notes = notes,
                        IsActive = ParseActive(GetValue(node, "IsActive")),
                        IsFavorite = isFav
                    });
                    RdpFileService.ApplyMetadata(candidates[^1], GetValue(node, "RdpConfiguration"));
                }


        return candidates;
    }
    internal static string GetValue(XElement element, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            var attr = element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attr != null && !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value.Trim();

            var child = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child != null && !string.IsNullOrWhiteSpace(child.Value))
                return child.Value.Trim();
        }
        return string.Empty;
    }


}
