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
    private record RdpCredentialHandoff(string EffectiveUser, DateTime StartedAtUtc);

    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly SqliteRepository _repository;
    private readonly ICredentialSecretReader _vaultService;
    private readonly AppLogger _logger;
    private readonly IProcessRunner _processRunner;
    private readonly ConnectionPlanBuilder _planBuilder;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RdpCredentialHandoff> _rdpCredentialHandoffs = new(StringComparer.OrdinalIgnoreCase);

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
        : this(paths, settingsService, repository, vaultService, logger,
            customProcessStarter == null ? null : new DelegateProcessRunner(customProcessStarter)) { }

    public ConnectionLauncherService(AppPaths paths, SettingsService settingsService, SqliteRepository repository,
        ICredentialSecretReader vaultService, AppLogger logger, IProcessRunner? processRunner = null)
    {
        _paths = paths;
        _settingsService = settingsService;
        _repository = repository;
        _vaultService = vaultService;
        _logger = logger;
        _processRunner = processRunner ?? new SystemProcessRunner();
        _planBuilder = new ConnectionPlanBuilder(paths);
    }

    public ConnectionLaunchPlan BuildLaunchPlan(ConnectionEntry entry, SavedCredential? credential = null, string? decryptedPassword = null)
        => _planBuilder.Build(entry, _settingsService.Current, credential, decryptedPassword);

    public ConnectionLaunchPlan BuildLaunchPlan(ConnectionEntry entry, SavedCredential? credential, bool hasDecryptedPassword)
        => _planBuilder.Build(entry, _settingsService.Current, credential, hasDecryptedPassword);

    public async Task<string> BuildSafeRdpPreviewAsync(ConnectionEntry snapshot)
    {
        SavedCredential? credential = null;
        var hasPassword = false;
        if (!string.IsNullOrWhiteSpace(snapshot.CredentialId))
        {
            credential = await _repository.GetCredentialByIdAsync(snapshot.CredentialId);
            if (credential != null)
            {
                try { hasPassword = !string.IsNullOrEmpty(await _vaultService.ReadSecretAsync(credential)); }
                catch { credential = null; }
            }
        }
        // The placeholder follows the same property generation, without emitting a password blob.
        var plan = BuildLaunchPlan(snapshot, credential, hasPassword);
        return RdpFileService.Sanitize(plan.TempRdpContent ?? "");
    }
    public async Task<ConnectionLaunchResult> LaunchAsync(ConnectionEntry entry)
    {
        // Validate persisted connection data before reading a linked secret.
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

        // Resolve the saved credential independently of the editing form.
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
        try
        {
            var securityNotice = plan.SecurityNotice;

            if (entry.Protocol == ConnectionProtocol.Rdp)
            {
                // Trata abertura concorrente para o mesmo servidor com usuários diferentes
                if (_rdpCredentialHandoffs.TryGetValue(cleanHostKey, out var activeHandshake))
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

                    _rdpCredentialHandoffs[cleanHostKey] = new RdpCredentialHandoff(plan.EffectiveUser, DateTime.UtcNow);
                }
                else
                {
                    // Conexão SEM senha salva: remove qualquer credencial TERMSRV anterior criada pelo ConectaTI
                    // para este host, evitando que o MSTSC utilize silenciosamente a senha de outro cadastro!
                    WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(entry.Host, entry.Port);
                    _rdpCredentialHandoffs[cleanHostKey] = new RdpCredentialHandoff(plan.EffectiveUser, DateTime.UtcNow);
                }

                // Grava o arquivo .rdp individual da conexão
                if (!string.IsNullOrWhiteSpace(plan.TempRdpFilePath) && !string.IsNullOrWhiteSpace(plan.TempRdpContent))
                {
                    _paths.EnsureDirectories();
                    _paths.CleanupTempRdpFiles();
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.TempRdpFilePath)!);
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

            var processId = _processRunner.Start(psi);

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

    // Compatibility entry point; argument parsing belongs to the plan builder.
    public static List<string> SplitArgumentsPreservingQuotes(string input) => CommandLineTokenizer.Split(input);
}
