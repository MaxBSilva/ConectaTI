using ConectaTI.App.Helpers;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services.Launchers;

// Builds arguments/content only; no database, process execution, timers or clipboard.
internal sealed class ConnectionPlanBuilder(AppPaths paths, IRdpPasswordEncoder? rdpPasswordEncoder = null)
{
    private readonly AppPaths _paths = paths;
    private readonly IRdpPasswordEncoder _rdpPasswordEncoder = rdpPasswordEncoder ?? new WindowsRdpPasswordEncoder();
    public ConnectionLaunchPlan Build(
        ConnectionEntry entry,
        AppSettings settings,
        SavedCredential? credential = null,
        string? decryptedPassword = null)
    {
        var rawUser = !string.IsNullOrWhiteSpace(entry.Username) ? entry.Username : credential?.Username ?? string.Empty;
        var rawDomain = !string.IsNullOrWhiteSpace(entry.Domain) ? entry.Domain : credential?.Domain ?? string.Empty;
        var userIdentity = UserIdentityFormatter.Normalize(
            ConnectionValidator.SanitizeSingleLineForRdp(rawUser),
            ConnectionValidator.SanitizeSingleLineForRdp(rawDomain));

        return entry.Protocol switch
        {
            ConnectionProtocol.Rdp => BuildRdpPlan(entry, userIdentity, settings, decryptedPassword),
            ConnectionProtocol.Ssh => BuildSshPlan(entry, userIdentity, settings, !string.IsNullOrEmpty(decryptedPassword)),
            ConnectionProtocol.Sftp or ConnectionProtocol.Scp => BuildWinScpPlan(entry, userIdentity, settings, !string.IsNullOrEmpty(decryptedPassword)),
            ConnectionProtocol.Vnc => BuildVncPlan(entry, userIdentity, settings, !string.IsNullOrEmpty(decryptedPassword)),
            _ => throw new NotSupportedException(L.Format("Protocolo não suportado: {0}", (entry.Protocol)))
        };
    }

    public ConnectionLaunchPlan Build(
        ConnectionEntry entry,
        AppSettings settings,
        SavedCredential? credential,
        bool hasDecryptedPassword)
    {
        return Build(entry, settings, credential, hasDecryptedPassword ? "__PROTECTED_PLACEHOLDER__" : null);
    }


    private ConnectionLaunchPlan BuildRdpPlan(
        ConnectionEntry entry,
        NormalizedUserIdentity identity,
        AppSettings settings,
        string? decryptedPassword)
    {
        var configuredExe = string.IsNullOrWhiteSpace(settings.MstscExecutablePath)
            ? ExternalClientDetector.GetDefaultMstscPath()
            : settings.MstscExecutablePath;

        var available = ExternalClientDetector.IsExecutableValid(configuredExe, out var resolvedExe);
        var endpointDisplay = ConnectionValidator.FormatHostAndPort(entry.Host, entry.Port);
        var hasPassword = !string.IsNullOrEmpty(decryptedPassword);

        var content = RdpFileService.Build(entry, identity, hasPassword);
        // Preserve the existing DPAPI and TERMSRV credential transport; never plaintext.
        if (hasPassword && decryptedPassword != "__PROTECTED_PLACEHOLDER__")
        {
            var protectedPassword = _rdpPasswordEncoder.Encode(decryptedPassword!);
            if (!string.IsNullOrEmpty(protectedPassword)) content += $"password 51:b:{protectedPassword}\r\n";
        }
        var rdpFilePath = RdpSessionFile.CreatePath(_paths.TempRdpDirectory, entry);
        var args = new List<string> { rdpFilePath };
        if (entry.RdpAdminSession)
        {
            args.Add("/admin");
        }

        var userDisplay = string.IsNullOrWhiteSpace(identity.EffectiveDownLevelLogonName)
            ? L.Text("(Solicitar no cliente)")
            : identity.EffectiveDownLevelLogonName;

        var notice = hasPassword
            ? L.Text("Nota: Caso uma política de segurança do domínio ou do servidor (como restrição de delegação NTLM/CredSSP) exija autenticação interativa, o Windows solicitará a confirmação na janela do MSTSC.")
            : L.Text("Conexão sem senha salva: o cliente de Área de Trabalho Remota (MSTSC) solicitará a senha ao conectar.");

        return new ConnectionLaunchPlan(
            available,
            resolvedExe,
            L.Text("Área de Trabalho Remota (MSTSC)"),
            args,
            rdpFilePath,
            content,
            endpointDisplay,
            identity.EffectiveDownLevelLogonName,
            L.Format("Cliente iniciado: Área de Trabalho Remota (MSTSC) para {0} (Usuário: {1}).", (endpointDisplay), (userDisplay)),
            notice,
            L.Format("O executável configurado para Área de Trabalho Remota ('{0}') não foi encontrado. Verifique o caminho em 'Configurar Executáveis' ou clique em 'Restaurar caminho padrão'.", (resolvedExe)));
    }

    private static ConnectionLaunchPlan BuildSshPlan(
        ConnectionEntry entry,
        NormalizedUserIdentity identity,
        AppSettings settings,
        bool hasPassword)
    {
        var effectivePref = entry.SshClient == SshClientPreference.Default
            ? settings.PreferredSshClient
            : entry.SshClient;

        var useOpenSsh = effectivePref == SshClientPreference.OpenSsh;
        if (!useOpenSsh &&
            entry.SshClient == SshClientPreference.Default &&
            !ExternalClientDetector.IsExecutableValid(settings.PuttyExecutablePath, out _) &&
            ExternalClientDetector.IsExecutableValid(settings.OpenSshExecutablePath, out _))
        {
            useOpenSsh = true;
        }

        var cleanHost = ConnectionValidator.StripIpv6Brackets(entry.Host);
        var endpoint = ConnectionValidator.FormatHostAndPort(entry.Host, entry.Port);
        var args = new List<string>();

        if (useOpenSsh)
        {
            var available = ExternalClientDetector.IsExecutableValid(settings.OpenSshExecutablePath, out var sshExe);

            args.Add("-p");
            args.Add(entry.Port.ToString());

            if (!string.IsNullOrWhiteSpace(identity.EffectiveSshUsername))
            {
                args.Add("-l");
                args.Add(identity.EffectiveSshUsername);
            }

            if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPath))
            {
                args.Add("-i");
                args.Add(entry.PrivateKeyPath.Trim());
            }

            args.Add("-o");
            args.Add($"ForwardAgent={(entry.SshUseAgent ? "yes" : "no")}");
            args.Add(cleanHost);

            var notice = hasPassword
                ? L.Text("O OpenSSH não recebe senhas por linha de comando por motivos de segurança. Utilize autenticação por chave ou informe a senha no prompt do terminal.")
                : L.Text("Autenticação via chave privada / agente SSH ou solicitação interativa no terminal.");

            return new ConnectionLaunchPlan(
                available,
                sshExe,
                "OpenSSH (ssh.exe)",
                args,
                null,
                null,
                endpoint,
                identity.EffectiveSshUsername,
                L.Format("Cliente iniciado: OpenSSH (ssh.exe) para {0}.", (endpoint)),
                notice,
                L.Text("O executável do OpenSSH (ssh.exe) não foi encontrado. Configure o caminho em 'Configurar Executáveis'."));
        }
        else
        {
            var available = ExternalClientDetector.IsExecutableValid(settings.PuttyExecutablePath, out var puttyExe);

            args.Add("-ssh");
            args.Add("-P");
            args.Add(entry.Port.ToString());

            if (!string.IsNullOrWhiteSpace(identity.EffectiveSshUsername))
            {
                args.Add("-l");
                args.Add(identity.EffectiveSshUsername);
            }

            if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPath))
            {
                args.Add("-i");
                args.Add(entry.PrivateKeyPath.Trim());
            }

            args.Add(entry.SshUseAgent ? "-agent" : "-noagent");
            args.Add(cleanHost);

            var notice = hasPassword
                ? L.Text("Por segurança, o ConectaTI não envia senhas em texto puro via argumento '-pw'. Utilize chave privada/Pageant ou o botão 'Copiar Senha' na tela de detalhes.")
                : L.Text("O PuTTY utilizará chave privada / Pageant ou solicitará a senha no terminal.");

            return new ConnectionLaunchPlan(
                available,
                puttyExe,
                "PuTTY (putty.exe)",
                args,
                null,
                null,
                endpoint,
                identity.EffectiveSshUsername,
                L.Format("Cliente iniciado: PuTTY (putty.exe) para {0}.", (endpoint)),
                notice,
                L.Text("O executável do PuTTY (putty.exe) não foi encontrado. Configure o caminho em 'Configurar Executáveis' ou altere o cliente SSH para OpenSSH."));
        }
    }

    private static ConnectionLaunchPlan BuildWinScpPlan(
        ConnectionEntry entry,
        NormalizedUserIdentity identity,
        AppSettings settings,
        bool hasPassword)
    {
        var available = ExternalClientDetector.IsExecutableValid(settings.WinScpExecutablePath, out var exe);

        var scheme = entry.Protocol == ConnectionProtocol.Scp ? "scp" : "sftp";
        var formattedHostPort = ConnectionValidator.FormatHostAndPort(entry.Host, entry.Port);
        var userPrefix = !string.IsNullOrWhiteSpace(identity.EffectiveSshUsername)
            ? $"{Uri.EscapeDataString(identity.EffectiveSshUsername)}@"
            : string.Empty;

        var sessionUrl = $"{scheme}://{userPrefix}{formattedHostPort}/";
        var args = new List<string> { sessionUrl };

        if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPath))
        {
            args.Add($"/privatekey={entry.PrivateKeyPath.Trim()}");
        }

        var notice = hasPassword
            ? L.Text("Por segurança, a senha não é exposta na URL de linha de comando do WinSCP. Utilize chave privada ou o botão 'Copiar Senha'.")
            : L.Text("O WinSCP utilizará chave privada / Pageant ou solicitará a senha ao conectar.");

        return new ConnectionLaunchPlan(
            available,
            exe,
            $"WinSCP ({scheme.ToUpperInvariant()})",
            args,
            null,
            null,
            formattedHostPort,
            identity.EffectiveSshUsername,
            L.Format("Cliente iniciado: WinSCP ({0}) para {1}.", (scheme.ToUpperInvariant()), (formattedHostPort)),
            notice,
            L.Text("O executável do WinSCP (WinSCP.exe) não foi encontrado. Localize o executável na tela 'Configurar Executáveis'."));
    }

    private static ConnectionLaunchPlan BuildVncPlan(
        ConnectionEntry entry,
        NormalizedUserIdentity identity,
        AppSettings settings,
        bool hasPassword)
    {
        var available = ExternalClientDetector.IsExecutableValid(settings.VncExecutablePath, out var exe);

        var cleanHost = ConnectionValidator.StripIpv6Brackets(entry.Host);
        var isIpv6 = cleanHost.Contains(':');
        var hostForColonFormat = isIpv6 ? $"[{cleanHost}]" : cleanHost;
        var endpoint = ConnectionValidator.FormatHostAndPort(entry.Host, entry.Port);

        var args = new List<string>();
        switch (settings.VncFlavor)
        {
            case VncViewerFlavor.TigerRealVnc:
                args.Add($"{hostForColonFormat}::{entry.Port}");
                break;

            case VncViewerFlavor.TightVnc:
                args.Add($"-host={cleanHost}");
                args.Add($"-port={entry.Port}");
                break;

            case VncViewerFlavor.UltraVnc:
                args.Add($"{hostForColonFormat}:{entry.Port}");
                break;

            case VncViewerFlavor.Custom:
                var template = string.IsNullOrWhiteSpace(settings.VncCustomArgumentsTemplate)
                    ? "{host}:{port}"
                    : settings.VncCustomArgumentsTemplate;
                foreach (var token in CommandLineTokenizer.Split(template))
                {
                    var expanded = token
                        .Replace("{host}", cleanHost, StringComparison.OrdinalIgnoreCase)
                        .Replace("{port}", entry.Port.ToString(), StringComparison.OrdinalIgnoreCase)
                        .Replace("{user}", identity.CleanUsername, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(expanded))
                        args.Add(expanded);
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(entry.VncParameters))
        {
            foreach (var extra in CommandLineTokenizer.Split(entry.VncParameters))
            {
                if (extra.StartsWith("-password", StringComparison.OrdinalIgnoreCase) ||
                    extra.StartsWith("/password", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                args.Add(extra);
            }
        }

        var notice = hasPassword
            ? L.Text("O cliente VNC solicitará a senha ao abrir (senhas não são passadas em texto puro por argumentos).")
            : L.Text("O cliente VNC solicitará a senha caso o servidor exija autenticação.");

        return new ConnectionLaunchPlan(
            available,
            exe,
            L.Format("Cliente VNC ({0})", (settings.VncFlavor)),
            args,
            null,
            null,
            endpoint,
            identity.CleanUsername,
            L.Format("Cliente iniciado: VNC para {0}.", (endpoint)),
            notice,
            L.Text("Nenhum cliente VNC foi encontrado. Configure o caminho do executável VNC em 'Configurar Executáveis'."));
    }


}
