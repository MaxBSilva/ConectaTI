using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

namespace ConectaTI.App.Services.Launchers;

public record ConnectionLaunchPlan(
    bool IsExecutableAvailable,
    string ExecutablePath,
    string ClientDisplayName,
    IReadOnlyList<string> Arguments,
    string? TempRdpFilePath,
    string? TempRdpContent,
    string EffectiveEndpoint,
    string EffectiveUser,
    string StatusMessage,
    string SecurityNotice,
    string MissingExecutableError);

public record ConnectionLaunchResult(
    bool Started,
    int? ProcessId,
    string ExecutablePath,
    string EffectiveEndpoint,
    string EffectiveUser,
    string? GeneratedRdpFilePath,
    string StatusMessage,
    string SecurityNotice,
    string ErrorMessage,
    bool IsMissingExecutable);

/// <summary>
/// Constrói argumentos de execução seguros (via ProcessStartInfo.ArgumentList, nunca concatenando em shell)
/// e inicia os clientes externos configurados (mstsc.exe, PuTTY, OpenSSH, WinSCP, VNC).
/// - Nunca transmite senhas em texto puro em argumentos, URLs, logs ou arquivos.
/// - Não utiliza cmdkey, SendKeys, cliques automatizados ou área de transferência ao conectar.
/// - Coordena o ciclo de vida das credenciais RDP no Gerenciador de Credenciais do Windows (TERMSRV),
///   preservando credenciais preexistentes do Windows e evitando troca de credenciais em conexões ao mesmo servidor.
/// </summary>
public class ConnectionLauncherService
{
    private record ActiveRdpHandshake(string ConnectionId, string EffectiveUser, DateTime StartedAtUtc);

    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;
    private readonly ClipboardService _clipboardService;
    private readonly AppLogger _logger;
    private readonly Func<ProcessStartInfo, int?>? _customProcessStarter;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveRdpHandshake> _activeRdpHandshakes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Janela mínima para evitar que duas conexões simultâneas para o mesmo servidor com usuários diferentes
    /// sobrescrevam a credencial TERMSRV uma da outra antes do cliente MSTSC ler a credencial inicial.
    /// </summary>
    public TimeSpan SameHostConflictWindow { get; set; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan SessionCredentialCleanupDelay { get; set; } = TimeSpan.FromSeconds(12);

    public ConnectionLauncherService(
        AppPaths paths,
        SettingsService settingsService,
        SqliteRepository repository,
        CredentialVaultService vaultService,
        ClipboardService clipboardService,
        AppLogger logger,
        Func<ProcessStartInfo, int?>? customProcessStarter = null)
    {
        _paths = paths;
        _settingsService = settingsService;
        _repository = repository;
        _vaultService = vaultService;
        _clipboardService = clipboardService;
        _logger = logger;
        _customProcessStarter = customProcessStarter;
    }

    public ConnectionLaunchPlan BuildLaunchPlan(
        ConnectionEntry entry,
        SavedCredential? credential = null,
        string? decryptedPassword = null)
    {
        var settings = _settingsService.Current;

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

    public ConnectionLaunchPlan BuildLaunchPlan(
        ConnectionEntry entry,
        SavedCredential? credential,
        bool hasDecryptedPassword)
    {
        return BuildLaunchPlan(entry, credential, hasDecryptedPassword ? "__PROTECTED_PLACEHOLDER__" : null);
    }

    public async Task<ConnectionLaunchResult> LaunchAsync(ConnectionEntry entry)
    {
        // Request the session only when this connection actually uses a saved secret.

        // 2. Validação completa do cadastro (incluindo proteção contra quebras de linha)
        var errors = ConnectionValidator.ValidateConnection(entry);
        if (errors.Count > 0)
        {
            return new ConnectionLaunchResult(
                false,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                L.Text("Dados inválidos no cadastro."),
                string.Empty,
                string.Join(Environment.NewLine, errors),
                false);
        }

        // 3. Obtém credencial salva vinculada ao cadastro (se houver)
        SavedCredential? credential = null;
        var decryptedPassword = string.Empty;
        var unavailableSecretNotice = string.Empty;

        if (!string.IsNullOrWhiteSpace(entry.CredentialId))
        {
            credential = await _repository.GetCredentialByIdAsync(entry.CredentialId);
            if (credential != null)
            {
                try
                {
                    decryptedPassword = await _vaultService.ReadSecretAsync(credential);
                }
                catch (Exception ex)
                {
                    _logger.RecordFailure("Launch.Credentials.Read",ex);
                    unavailableSecretNotice=CredentialVaultService.RecoveryMessage(ex)+" "+
                        L.Text("O cliente remoto solicitará as credenciais manualmente.");
                    credential=null;

                }
            }
        }

        var hasPassword = !string.IsNullOrEmpty(decryptedPassword);
        var plan = BuildLaunchPlan(entry, credential, hasPassword ? decryptedPassword : null);
        if(unavailableSecretNotice.Length>0) plan=plan with {SecurityNotice=unavailableSecretNotice};

        if (!plan.IsExecutableAvailable)
        {
            _logger.Warn("Launcher", $"Executável ausente para {entry.Protocol}: '{plan.ExecutablePath}'");
            return new ConnectionLaunchResult(
                false,
                null,
                plan.ExecutablePath,
                plan.EffectiveEndpoint,
                plan.EffectiveUser,
                null,
                L.Text("Executável do cliente não encontrado."),
                string.Empty,
                plan.MissingExecutableError,
                true);
        }

        var cleanHostKey = ConnectionValidator.StripIpv6Brackets(entry.Host).ToLowerInvariant();
        var hostSemaphore = _hostLocks.GetOrAdd(cleanHostKey, _ => new SemaphoreSlim(1, 1));

        await hostSemaphore.WaitAsync();
        FileStream? rdpSessionLease = null;
        try
        {
            var securityNotice = plan.SecurityNotice;

            if (entry.Protocol == ConnectionProtocol.Rdp)
            {
                // Trata abertura concorrente para o mesmo servidor com usuários diferentes
                if (_activeRdpHandshakes.TryGetValue(cleanHostKey, out var activeHandshake))
                {
                    var elapsed = DateTime.UtcNow - activeHandshake.StartedAtUtc;
                    if (elapsed < SameHostConflictWindow &&
                        !string.Equals(activeHandshake.EffectiveUser, plan.EffectiveUser, StringComparison.OrdinalIgnoreCase))
                    {
                        var remaining = SameHostConflictWindow - elapsed;
                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining);
                        }
                    }
                }

                // Provisiona ou limpa a credencial TERMSRV no Gerenciador de Credenciais do Windows
                if (hasPassword && _settingsService.Current.RegisterRdpCredentialsInWindowsVault)
                {
                    var provResult = WindowsCredentialVaultInterop.ProvisionRdpSessionCredentials(
                        entry.Id,
                        entry.Host,
                        entry.Port,
                        plan.EffectiveUser,
                        decryptedPassword);

                    if (!string.IsNullOrWhiteSpace(provResult.NoticeMessage))
                    {
                        securityNotice = provResult.NoticeMessage + " " + plan.SecurityNotice;
                    }

                    _activeRdpHandshakes[cleanHostKey] = new ActiveRdpHandshake(entry.Id, plan.EffectiveUser, DateTime.UtcNow);
                }
                else
                {
                    // Conexão SEM senha salva: remove qualquer credencial TERMSRV anterior criada pelo ConectaTI
                    // para este host, evitando que o MSTSC utilize silenciosamente a senha de outro cadastro!
                    WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(entry.Host, entry.Port);
                    _activeRdpHandshakes[cleanHostKey] = new ActiveRdpHandshake(entry.Id, plan.EffectiveUser, DateTime.UtcNow);
                }

                // Grava o arquivo .rdp individual da conexão
                if (!string.IsNullOrWhiteSpace(plan.TempRdpFilePath) && !string.IsNullOrWhiteSpace(plan.TempRdpContent))
                {
                    _paths.EnsureDirectories();
                    _paths.CleanupTempRdpFiles();
                    rdpSessionLease = RdpSessionFiles.AcquireLease(plan.TempRdpFilePath);
                    await File.WriteAllTextAsync(plan.TempRdpFilePath, plan.TempRdpContent, new UTF8Encoding(true));
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = plan.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            foreach (var arg in plan.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            int? processId;
            if (_customProcessStarter is not null)
            {
                processId = _customProcessStarter(psi);
            }
            else
            {
                var process = Process.Start(psi);
                processId = process?.Id;
                if (process is not null && rdpSessionLease is not null)
                {
                    // Conserva o arquivo até o fim deste processo exato, sem buscar janelas/PIDs.
                    _ = RdpSessionFiles.CleanupOnExitAsync(process, rdpSessionLease, plan.TempRdpFilePath!);
                    rdpSessionLease = null; // A tarefa passa a ser dona do lease e do Process.
                }
                else
                {
                    process?.Dispose();
                }
            }

            var openedAt = DateTime.UtcNow;
            entry.LastOpenedAtUtc = openedAt;
            await _repository.MarkConnectionOpenedAsync(entry.Id, openedAt);

            if (entry.Protocol == ConnectionProtocol.Rdp && hasPassword && _settingsService.Current.RegisterRdpCredentialsInWindowsVault)
            {
                // Agenda limpeza automática do ciclo de vida da credencial temporária do ConectaTI
                // (remove apenas se o Comment ainda pertencer a este ConnectionId)
                _ = ScheduleRdpCredentialCleanupAsync(entry.Id, entry.Host, entry.Port, SessionCredentialCleanupDelay);
            }

            _logger.Info(
                "Launcher",
                $"Cliente iniciado: {plan.ClientDisplayName} ({plan.ExecutablePath}) para {plan.EffectiveEndpoint} [Usuário: '{plan.EffectiveUser}', Conexão: '{entry.Name}', PID: {processId}]");

            return new ConnectionLaunchResult(
                true,
                processId,
                plan.ExecutablePath,
                plan.EffectiveEndpoint,
                plan.EffectiveUser,
                plan.TempRdpFilePath,
                plan.StatusMessage,
                securityNotice,
                string.Empty,
                false);
        }
        catch (Exception ex)
        {
            _logger.Error("Launcher", $"Falha ao iniciar cliente {plan.ClientDisplayName} para '{entry.Name}'.", ex);
            return new ConnectionLaunchResult(
                false,
                null,
                plan.ExecutablePath,
                plan.EffectiveEndpoint,
                plan.EffectiveUser,
                null,
                L.Text("Erro ao iniciar cliente externo."),
                string.Empty,
                L.Format("Não foi possível iniciar o executável '{0}': {1}", (plan.ExecutablePath), (ex.Message)),
                false);
        }
        finally
        {
            rdpSessionLease?.Dispose();
            hostSemaphore.Release();
        }
    }

    private async Task ScheduleRdpCredentialCleanupAsync(string connectionId, string host, int port, TimeSpan delay)
    {
        try
        {
            if (delay <= TimeSpan.Zero)
                return;

            await Task.Delay(delay);
            WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host, port, onlyIfConnectionId: connectionId);
        }
        catch
        {
            // Ignora falha em limpeza de segundo plano
        }
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
        var fullAddress = ConnectionValidator.FormatRdpFullAddress(entry.Host, entry.Port);
        var endpointDisplay = ConnectionValidator.FormatHostAndPort(entry.Host, entry.Port);
        var hasPassword = !string.IsNullOrEmpty(decryptedPassword);

        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{fullAddress}");
        sb.AppendLine($"server port:i:{entry.Port}");
        sb.AppendLine($"screen mode id:i:{(entry.RdpFullScreen ? 2 : 1)}");
        if (!entry.RdpFullScreen)
        {
            sb.AppendLine($"desktopwidth:i:{entry.RdpWidth}");
            sb.AppendLine($"desktopheight:i:{entry.RdpHeight}");
        }
        sb.AppendLine($"use multimon:i:{(entry.RdpUseMultimon ? 1 : 0)}");

        if (!string.IsNullOrWhiteSpace(identity.EffectiveDownLevelLogonName))
        {
            if (identity.IsUpn)
            {
                sb.AppendLine($"username:s:{ConnectionValidator.SanitizeSingleLineForRdp(identity.RawUsername)}");
            }
            else
            {
                sb.AppendLine($"username:s:{ConnectionValidator.SanitizeSingleLineForRdp(identity.CleanUsername)}");
                if (!string.IsNullOrWhiteSpace(identity.Domain))
                {
                    sb.AppendLine($"domain:s:{ConnectionValidator.SanitizeSingleLineForRdp(identity.Domain)}");
                }
            }
        }

        if (hasPassword && decryptedPassword != "__PROTECTED_PLACEHOLDER__")
        {
            var dpapiHex = WindowsCredentialVaultInterop.EncryptRdpPassword51Hex(decryptedPassword!);
            if (!string.IsNullOrEmpty(dpapiHex))
            {
                sb.AppendLine($"password 51:b:{dpapiHex}");
            }
        }

        // Mantém NLA e validação de certificado ativos
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine($"prompt for credentials:i:{(hasPassword ? 0 : 1)}");
        sb.AppendLine($"prompt for credentials on client:i:{(hasPassword ? 0 : 1)}");

        if (entry.RdpAdminSession)
        {
            sb.AppendLine("administrative session:i:1");
        }

        var rdpFilePath = RdpSessionFiles.CreatePath(_paths.TempRdpDirectory, entry.Name, entry.ClientName);

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
            sb.ToString(),
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
                foreach (var token in SplitArgumentsPreservingQuotes(template))
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
            foreach (var extra in SplitArgumentsPreservingQuotes(entry.VncParameters))
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

    public static List<string> SplitArgumentsPreservingQuotes(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }
}
