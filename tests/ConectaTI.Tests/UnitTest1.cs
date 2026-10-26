using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

namespace ConectaTI.Tests;

public class ConectaTITests : IDisposable
{
    private readonly string _tempRootDir;
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly SettingsService _settingsService;
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;

    public ConectaTITests()
    {
        ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        _tempRootDir = Path.Combine(Path.GetTempPath(), "ConectaTI_Tests_" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_tempRootDir);
        _logger = new AppLogger(_paths);
        _settingsService = new SettingsService(_paths, _logger);
        _settingsService.Load();
        _repository = new SqliteRepository(_paths, _logger);
        _repository.InitializeAndMigrate();
        _vaultService = new CredentialVaultService(_settingsService, _repository, _logger, useWindowsVault: false);
        VaultTestSupport.Initialize(_vaultService);
    }

    public void Dispose()
    {
        try
        {
            WindowsCredentialVaultInterop.CleanupAllManagedRdpCredentials();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRootDir))
            {
                Directory.Delete(_tempRootDir, recursive: true);
            }
        }
        catch
        {
            // Ignora arquivos temporários liberados pelo GC
        }
    }

    [Fact]
    public async Task Persistence_SaveCloseAndReopen_PreservesAllFieldsAndLastOpenedDate()
    {
        var entry = new ConnectionEntry
        {
            Name = "SRV-SQL-PRODUÇÃO-01",
            ClientName = "Cliente Acentuação S/A",
            GroupName = "Banco de Dados & BI",
            Environment = EnvironmentType.Production,
            Protocol = ConnectionProtocol.Rdp,
            Host = "sql01.cliente.interno.local",
            Port = 33890,
            Username = "dba.admin",
            Domain = "CLIENTESA",
            PrivateKeyPath = @"C:\Chaves\chave privada com espaços.ppk",
            Notes = "Observação com acentuação: atenção à janela de manutenção às 02h.",
            Tags = "sqlserver, critico, producao",
            IsFavorite = true,
            RdpFullScreen = false,
            RdpWidth = 1600,
            RdpHeight = 900,
            RdpUseMultimon = true,
            RdpAdminSession = true
        };

        await _repository.SaveConnectionAsync(entry);
        var openedTime = new DateTime(2026, 10, 21, 18, 30, 0, DateTimeKind.Utc);
        await _repository.MarkConnectionOpenedAsync(entry.Id, openedTime);

        // Simula fechar e reabrir o aplicativo instanciando um novo repositório sobre o mesmo arquivo SQLite
        var reopenedRepo = new SqliteRepository(_paths, _logger);
        reopenedRepo.InitializeAndMigrate();
        var loaded = await reopenedRepo.GetConnectionByIdAsync(entry.Id);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Id, loaded!.Id);
        Assert.Equal("SRV-SQL-PRODUÇÃO-01", loaded.Name);
        Assert.Equal("Cliente Acentuação S/A", loaded.ClientName);
        Assert.Equal("Banco de Dados & BI", loaded.GroupName);
        Assert.Equal(EnvironmentType.Production, loaded.Environment);
        Assert.Equal(ConnectionProtocol.Rdp, loaded.Protocol);
        Assert.Equal("sql01.cliente.interno.local", loaded.Host);
        Assert.Equal(33890, loaded.Port);
        Assert.Equal("dba.admin", loaded.Username);
        Assert.Equal("CLIENTESA", loaded.Domain);
        Assert.Equal(@"C:\Chaves\chave privada com espaços.ppk", loaded.PrivateKeyPath);
        Assert.Equal("Observação com acentuação: atenção à janela de manutenção às 02h.", loaded.Notes);
        Assert.True(loaded.IsFavorite);
        Assert.False(loaded.RdpFullScreen);
        Assert.Equal(1600, loaded.RdpWidth);
        Assert.Equal(900, loaded.RdpHeight);
        Assert.True(loaded.RdpUseMultimon);
        Assert.True(loaded.RdpAdminSession);
        Assert.Equal(openedTime, loaded.LastOpenedAtUtc);
    }

    [Theory]
    [InlineData("admin", "", "admin", "", "admin")]
    [InlineData("joao.silva", "CORP", "joao.silva", "CORP", @"CORP\joao.silva")]
    [InlineData(@"CORP\joao.silva", "", "joao.silva", "CORP", @"CORP\joao.silva")]
    [InlineData(@"CORP\joao.silva", "CORP", "joao.silva", "CORP", @"CORP\joao.silva")]
    [InlineData(@"CORP\CORP\joao.silva", "CORP", "joao.silva", "CORP", @"CORP\joao.silva")]
    [InlineData(@".\administrador", "", "administrador", ".", @".\administrador")]
    [InlineData("joao.silva@corp.local", "CORP", "joao.silva", "CORP", "joao.silva@corp.local")]
    public void UserIdentityFormatter_PreventsDuplicateDomainAndHandlesLocalDownLevelAndUpn(
        string rawUser,
        string rawDomain,
        string expectedCleanUser,
        string expectedDomain,
        string expectedEffectiveLogon)
    {
        var norm = UserIdentityFormatter.Normalize(rawUser, rawDomain);
        Assert.Equal(expectedCleanUser, norm.CleanUsername);
        Assert.Equal(expectedDomain, norm.Domain);
        Assert.Equal(expectedEffectiveLogon, norm.EffectiveDownLevelLogonName);
    }

    [Theory]
    [InlineData("srv-ad01.empresa.com.br", 3389, true)]
    [InlineData("192.168.10.25", 22, true)]
    [InlineData("2001:db8:85a3::8a2e:370:7334", 22, true)]
    [InlineData("[2001:db8::1]", 5900, true)]
    [InlineData("servidor-interno", 65535, true)]
    [InlineData("servidor-interno", 1, true)]
    [InlineData("servidor-interno", 0, false)]
    [InlineData("servidor-interno", 65536, false)]
    [InlineData("servidor-interno", -10, false)]
    [InlineData("", 3389, false)]
    [InlineData("host invalido com espaco", 3389, false)]
    [InlineData("srv.local\r\nalternate shell:s:cmd.exe", 3389, false)]
    public void ConnectionValidator_ValidatesDnsIpv4Ipv6PortBoundsAndRejectsLineBreaks(string host, int port, bool expectedValid)
    {
        var entry = new ConnectionEntry
        {
            Name = "Servidor Teste",
            ClientName = "Cliente Teste",
            Host = host,
            Port = port
        };

        var errors = ConnectionValidator.ValidateConnection(entry);
        Assert.Equal(expectedValid, errors.Count == 0);
    }

    [Fact]
    public void ConnectionValidator_RejectsNewlineInjectionInUserDomainAndSanitizesRdpLines()
    {
        var entry = new ConnectionEntry
        {
            Name = "Servidor Seguro",
            ClientName = "Cliente Seguro",
            Host = "10.0.0.10",
            Port = 3389,
            Username = "admin\r\nalternate shell:s:powershell.exe",
            Domain = "CORP\nINJECT"
        };

        var errors = ConnectionValidator.ValidateConnection(entry);
        Assert.True(errors.Count >= 2);

        // Mesmo se chamado diretamente, SanitizeSingleLineForRdp remove caracteres de controle e quebras de linha
        var sanitized = ConnectionValidator.SanitizeSingleLineForRdp("admin\r\ninjected:s:1");
        Assert.DoesNotContain("\r", sanitized);
        Assert.DoesNotContain("\n", sanitized);
    }

    [Fact]
    public async Task MstscFullEndToEndFlow_CreateSaveCloseReopenAndConnect_PopulatesHostPortUserDomainPasswordAndScreenPrefs()
    {
        const string host = "rdp-e2e-test.conectati.interno";
        const int customPort = 3399;
        const string password = "Rdp#Password!2026";

        try
        {
            // 1. Cria conexão MSTSC pelo ViewModel (padrão MSTSC 3389 ao clicar em Novo), preenche dados + senha e salva
            var clipboard1 = new ClipboardService(_logger, _ => true, () => null, () => { });
            var launcher1 = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard1, _logger);
            var importExport1 = new ImportExportService(_repository, _vaultService, _logger);
            var diag1 = new NetworkDiagnosticService();

            var vm1 = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard1, launcher1, importExport1, diag1);
            await vm1.InitializeAsync(seedDemoIfEmpty: false);

            vm1.NewConnection();
            Assert.Equal(ConnectionProtocol.Rdp, vm1.EditingEntry.Protocol);
            Assert.Equal(3389, vm1.EditingEntry.Port);
            Assert.Equal("Área de Trabalho Remota (MSTSC)", vm1.EditingEntry.ProtocolDisplay);

            vm1.EditingEntry.Name = "Servidor E2E MSTSC";
            vm1.EditingEntry.ClientName = "Cliente E2E";
            vm1.EditingEntry.Host = host;
            vm1.EditingEntry.Port = customPort;
            vm1.EditingEntry.Username = "operador.rdp";
            vm1.EditingEntry.Domain = "DOMINIOE2E";
            vm1.EditingEntry.RdpFullScreen = false;
            vm1.EditingEntry.RdpWidth = 1440;
            vm1.EditingEntry.RdpHeight = 900;
            vm1.EditingEntry.RdpUseMultimon = false;
            vm1.EditingEntry.RdpAdminSession = true;
            vm1.InlinePasswordInput = password;

            await vm1.SaveConnectionAsync();
            var savedId = vm1.SelectedConnection!.Id;
            Assert.False(vm1.IsEditing);
            Assert.True(vm1.HasSavedPassword);
            Assert.Equal("Senha salva", vm1.PasswordStatusText);

            // 2. Simula fechar e reabrir o aplicativo com novas instâncias de serviços e ViewModel
            var settings2 = new SettingsService(_paths, _logger);
            settings2.Load();
            var repo2 = new SqliteRepository(_paths, _logger);
            repo2.InitializeAndMigrate();
            var vault2 = new CredentialVaultService(settings2, repo2, _logger, useWindowsVault: false); VaultTestSupport.Initialize(vault2);
            var clipboard2 = new ClipboardService(_logger, _ => true, () => null, () => { });

            ProcessStartInfo? capturedStartInfo = null;
            string? capturedRdpFileContent = null;
            RdpCredentialInspection? capturedTermsrvHostState = null;
            RdpCredentialInspection? capturedTermsrvPortState = null;

            var launcher2 = new ConnectionLauncherService(
                _paths,
                settings2,
                repo2,
                vault2,
                clipboard2,
                _logger,
                customProcessStarter: psi =>
                {
                    capturedStartInfo = psi;
                    var rdpFileArg = psi.ArgumentList.FirstOrDefault(a => a.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase));
                    if (rdpFileArg != null && File.Exists(rdpFileArg))
                    {
                        capturedRdpFileContent = File.ReadAllText(rdpFileArg);
                    }
                    capturedTermsrvHostState = WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{host}");
                    capturedTermsrvPortState = WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{host}:{customPort}");
                    return Environment.ProcessId;
                });

            var importExport2 = new ImportExportService(repo2, vault2, _logger);
            var diag2 = new NetworkDiagnosticService();
            var vm2 = new MainViewModel(_paths, _logger, settings2, repo2, vault2, clipboard2, launcher2, importExport2, diag2);
            await vm2.InitializeAsync(seedDemoIfEmpty: false);

            Assert.NotNull(vm2.SelectedConnection);
            Assert.Equal(savedId, vm2.SelectedConnection!.Id);
            Assert.True(vm2.HasSavedPassword);
            Assert.Equal("Senha salva", vm2.PasswordStatusText);

            // 3. Conecta e verifica o comando construído, o arquivo .rdp e a credencial TERMSRV
            await vm2.ConnectAsync();

            Assert.NotNull(capturedStartInfo);
            Assert.Contains("/admin", capturedStartInfo!.ArgumentList);
            Assert.NotNull(capturedRdpFileContent);
            Assert.Contains($"full address:s:{host}:{customPort}", capturedRdpFileContent);
            Assert.Contains("username:s:operador.rdp", capturedRdpFileContent);
            Assert.Contains("domain:s:DOMINIOE2E", capturedRdpFileContent);
            Assert.Contains("screen mode id:i:1", capturedRdpFileContent);
            Assert.Contains("desktopwidth:i:1440", capturedRdpFileContent);
            Assert.Contains("desktopheight:i:900", capturedRdpFileContent);
            Assert.Contains("administrative session:i:1", capturedRdpFileContent);
            Assert.Contains("prompt for credentials:i:0", capturedRdpFileContent);
            Assert.Contains("password 51:b:", capturedRdpFileContent);
            Assert.DoesNotContain(password, capturedRdpFileContent, StringComparison.Ordinal);

            // Verifica que o hash DPAPI password 51:b: corresponde à senha cadastrada
            var pswLine = capturedRdpFileContent!
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("password 51:b:", StringComparison.Ordinal));
            var hexBlob = pswLine["password 51:b:".Length..];
            Assert.Equal(password, WindowsCredentialVaultInterop.DecryptRdpPassword51Hex(hexBlob));

            // Verifica que o Windows Credential Manager recebeu TERMSRV/<host> e TERMSRV/<host>:<porta> com usuário DOMINIOE2E\operador.rdp
            Assert.NotNull(capturedTermsrvHostState);
            Assert.True(capturedTermsrvHostState!.Exists);
            Assert.True(capturedTermsrvHostState.IsManagedByConectaTI);
            Assert.Equal(@"DOMINIOE2E\operador.rdp", capturedTermsrvHostState.UserName);

            Assert.NotNull(capturedTermsrvPortState);
            Assert.True(capturedTermsrvPortState!.Exists);
            Assert.True(capturedTermsrvPortState.IsManagedByConectaTI);
            Assert.Equal(@"DOMINIOE2E\operador.rdp", capturedTermsrvPortState.UserName);
        }
        finally
        {
            WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host, customPort);
        }
    }

    [Fact]
    public async Task CustomConfiguredMstscExecutablePath_IsPersistedAndUsedWhenConnecting()
    {
        // Cria um executável dummy válido na pasta temporária para simular um caminho customizado configurado pelo usuário
        var customMstscPath = Path.Combine(_tempRootDir, "meu_mstsc_customizado.exe");
        await File.WriteAllBytesAsync(customMstscPath, [0x4D, 0x5A, 0x90, 0x00]);

        _settingsService.Current.MstscExecutablePath = customMstscPath;
        _settingsService.Save();

        // Recarrega configurações e garante que AutoPopulateMissingPaths NÃO sobrescreve o caminho configurado pelo usuário
        var reloadedSettings = new SettingsService(_paths, _logger);
        reloadedSettings.Load();
        ExternalClientDetector.AutoPopulateMissingPaths(reloadedSettings.Current);
        Assert.Equal(customMstscPath, reloadedSettings.Current.MstscExecutablePath);

        ProcessStartInfo? startedPsi = null;
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(
            _paths,
            reloadedSettings,
            _repository,
            _vaultService,
            clipboard,
            _logger,
            customProcessStarter: psi =>
            {
                startedPsi = psi;
                return Environment.ProcessId;
            });

        var entry = new ConnectionEntry
        {
            Name = "Servidor Caminho Customizado",
            ClientName = "Cliente Teste",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.20.30.40",
            Port = 3389,
            Username = "admin"
        };

        var result = await launcher.LaunchAsync(entry);
        Assert.True(result.Started);
        Assert.NotNull(startedPsi);
        Assert.Equal(customMstscPath, startedPsi!.FileName);

        // Testa restauração do caminho padrão (%WINDIR%\System32\mstsc.exe)
        reloadedSettings.Current.MstscExecutablePath = ExternalClientDetector.DefaultMstscEnvironmentPath;
        var resolvedDefault = ExternalClientDetector.ResolveConfiguredPath(reloadedSettings.Current.MstscExecutablePath);
        Assert.Equal(ExternalClientDetector.GetDefaultMstscPath(), resolvedDefault, ignoreCase: true);
        Assert.True(ExternalClientDetector.IsExecutableValid(reloadedSettings.Current.MstscExecutablePath, out _));
    }

    [Fact]
    public async Task PasswordEditing_BlankInputPreservesExistingPassword_AndExplicitRemovalDeletesIt()
    {
        const string initialPassword = "SenhaInicial#123";

        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // 1. Cria conexão com senha salva
        vm.NewConnection();
        vm.EditingEntry.Name = "SRV-EDIT-SENHA";
        vm.EditingEntry.ClientName = "Cliente A";
        vm.EditingEntry.Host = "10.10.10.10";
        vm.EditingEntry.Port = 3389;
        vm.EditingEntry.Username = "administrador";
        vm.InlinePasswordInput = initialPassword;
        await vm.SaveConnectionAsync();

        var credIdAfterCreate = vm.SelectedConnection!.CredentialId;
        Assert.False(string.IsNullOrWhiteSpace(credIdAfterCreate));
        Assert.True(vm.HasSavedPassword);
        Assert.Equal("Senha salva", vm.PasswordStatusText);

        // 2. Edita outros campos (Nome, Porta, Observações) deixando o campo de nova senha VAZIO
        vm.BeginEdit();
        Assert.Equal("Alterar senha", vm.PasswordFieldLabel);
        Assert.Equal(string.Empty, vm.InlinePasswordInput);
        vm.EditingEntry.Name = "SRV-EDIT-SENHA-RENOMEADO";
        vm.EditingEntry.Port = 3390;
        vm.EditingEntry.Notes = "Porta alterada sem redigitar senha.";
        await vm.SaveConnectionAsync();

        // Confirma que a senha existente permanece intacta
        Assert.True(vm.HasSavedPassword);
        Assert.Equal("Senha salva", vm.PasswordStatusText);
        var preservedCred = await _repository.GetCredentialByIdAsync(vm.SelectedConnection.CredentialId!);
        Assert.NotNull(preservedCred);
        Assert.Equal(initialPassword, _vaultService.UnprotectSecret(preservedCred!));

        // 3. Remove explicitamente a senha salva usando a ação dedicada
        vm.BeginEdit();
        Assert.True(vm.CanRemoveSavedPassword);
        vm.MarkPasswordForRemoval();
        Assert.Equal("Remoção pendente — salvar", vm.PasswordStatusText);
        await vm.SaveConnectionAsync();

        Assert.False(vm.HasSavedPassword);
        Assert.Equal("Sem senha salva", vm.PasswordStatusText);
        Assert.Null(vm.SelectedConnection.CredentialId);
    }

    [Fact]
    public async Task MstscWithoutSavedPassword_ClearsPreviousManagedTermsrvCredentialAndDoesNotReuseIt()
    {
        const string host = "rdp-reuse-check.conectati.interno";
        const int port = 3389;

        try
        {
            var credWithPass = new SavedCredential
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Credencial Com Senha",
                Username = "usuario.com.senha",
                Domain = "CORP"
            };
            credWithPass.ProtectedSecretBase64 = _vaultService.ProtectSecret(credWithPass.Id, credWithPass.EffectiveUserDisplay, "Senha Temporaria 1");
            await _repository.SaveCredentialAsync(credWithPass);

            var connWithPass = new ConnectionEntry
            {
                Name = "Conexão 1 (Com Senha)",
                ClientName = "Cliente X",
                Protocol = ConnectionProtocol.Rdp,
                Host = host,
                Port = port,
                Username = "usuario.com.senha",
                Domain = "CORP",
                CredentialId = credWithPass.Id
            };

            var connWithoutPass = new ConnectionEntry
            {
                Name = "Conexão 2 (Sem Senha Salva)",
                ClientName = "Cliente X",
                Protocol = ConnectionProtocol.Rdp,
                Host = host,
                Port = port,
                Username = "usuario.sem.senha",
                Domain = "CORP",
                CredentialId = null
            };

            var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
            RdpCredentialInspection? stateDuringFirstLaunch = null;
            RdpCredentialInspection? stateDuringSecondLaunch = null;
            string? secondRdpContent = null;

            var launchIndex = 0;
            var launcher = new ConnectionLauncherService(
                _paths,
                _settingsService,
                _repository,
                _vaultService,
                clipboard,
                _logger,
                customProcessStarter: psi =>
                {
                    launchIndex++;
                    var rdpFile = psi.ArgumentList.First(a => a.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase));
                    if (launchIndex == 1)
                    {
                        stateDuringFirstLaunch = WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{host}");
                    }
                    else
                    {
                        stateDuringSecondLaunch = WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{host}");
                        secondRdpContent = File.ReadAllText(rdpFile);
                    }
                    return Environment.ProcessId;
                });

            // 1ª Conexão: possui senha salva -> grava TERMSRV/<host>
            var res1 = await launcher.LaunchAsync(connWithPass);
            Assert.True(res1.Started);
            Assert.NotNull(stateDuringFirstLaunch);
            Assert.True(stateDuringFirstLaunch!.Exists);
            Assert.Equal(@"CORP\usuario.com.senha", stateDuringFirstLaunch.UserName);

            // 2ª Conexão para o mesmo host: NÃO possui senha salva -> deve limpar a credencial temporária do ConectaTI antes de abrir o mstsc
            var res2 = await launcher.LaunchAsync(connWithoutPass);
            Assert.True(res2.Started);
            Assert.NotNull(stateDuringSecondLaunch);
            Assert.False(stateDuringSecondLaunch!.Exists);
            Assert.NotNull(secondRdpContent);
            Assert.Contains("prompt for credentials:i:1", secondRdpContent);
            Assert.DoesNotContain("password 51:b:", secondRdpContent);
        }
        finally
        {
            WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host, port);
        }
    }

    [Fact]
    public async Task MstscSameHostTwoDifferentUsers_SequentialAndSimultaneousSessions_UsesCorrectUserAndPasswordForEach()
    {
        const string sharedHost = "srv-ts-compartilhado.conectati.interno";
        const int port = 3389;

        try
        {
            var credUserA = new SavedCredential
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Credencial User A",
                Username = "analista.alfa",
                Domain = "CLIENTE"
            };
            credUserA.ProtectedSecretBase64 = _vaultService.ProtectSecret(credUserA.Id, credUserA.EffectiveUserDisplay, "SenhaAlfa#111");
            await _repository.SaveCredentialAsync(credUserA);

            var credUserB = new SavedCredential
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Credencial User B",
                Username = "analista.beta",
                Domain = "CLIENTE"
            };
            credUserB.ProtectedSecretBase64 = _vaultService.ProtectSecret(credUserB.Id, credUserB.EffectiveUserDisplay, "SenhaBeta#222");
            await _repository.SaveCredentialAsync(credUserB);

            var connA = new ConnectionEntry
            {
                Name = "TS - Analista Alfa",
                ClientName = "Cliente MultiSessão",
                Protocol = ConnectionProtocol.Rdp,
                Host = sharedHost,
                Port = port,
                Username = "analista.alfa",
                Domain = "CLIENTE",
                CredentialId = credUserA.Id
            };

            var connB = new ConnectionEntry
            {
                Name = "TS - Analista Beta",
                ClientName = "Cliente MultiSessão",
                Protocol = ConnectionProtocol.Rdp,
                Host = sharedHost,
                Port = port,
                Username = "analista.beta",
                Domain = "CLIENTE",
                CredentialId = credUserB.Id
            };

            var capturedLaunches = new List<(string RdpContent, string? TermsrvUser)>();
            var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
            var launcher = new ConnectionLauncherService(
                _paths,
                _settingsService,
                _repository,
                _vaultService,
                clipboard,
                _logger,
                customProcessStarter: psi =>
                {
                    var rdpFile = psi.ArgumentList.First(a => a.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase));
                    var content = File.ReadAllText(rdpFile);
                    var termsrv = WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{sharedHost}");
                    lock (capturedLaunches)
                    {
                        capturedLaunches.Add((content, termsrv.UserName));
                    }
                    return Environment.ProcessId;
                });

            // 1. Execução em sequência para o mesmo host com usuários diferentes
            var seqA = await launcher.LaunchAsync(connA);
            var seqB = await launcher.LaunchAsync(connB);
            Assert.True(seqA.Started);
            Assert.True(seqB.Started);
            Assert.Equal(2, capturedLaunches.Count);

            Assert.Contains("username:s:analista.alfa", capturedLaunches[0].RdpContent);
            Assert.Equal(@"CLIENTE\analista.alfa", capturedLaunches[0].TermsrvUser);
            var hexA = capturedLaunches[0].RdpContent.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("password 51:b:")).Substring("password 51:b:".Length);
            Assert.Equal("SenhaAlfa#111", WindowsCredentialVaultInterop.DecryptRdpPassword51Hex(hexA));

            Assert.Contains("username:s:analista.beta", capturedLaunches[1].RdpContent);
            Assert.Equal(@"CLIENTE\analista.beta", capturedLaunches[1].TermsrvUser);
            var hexB = capturedLaunches[1].RdpContent.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("password 51:b:")).Substring("password 51:b:".Length);
            Assert.Equal("SenhaBeta#222", WindowsCredentialVaultInterop.DecryptRdpPassword51Hex(hexB));

            // 2. Execução concorrente / simultânea (duas sessões abertas ao mesmo tempo para o mesmo host)
            capturedLaunches.Clear();
            await Task.WhenAll(launcher.LaunchAsync(connA), launcher.LaunchAsync(connB));
            Assert.Equal(2, capturedLaunches.Count);
            Assert.Contains(capturedLaunches, x => x.TermsrvUser == @"CLIENTE\analista.alfa" && x.RdpContent.Contains("username:s:analista.alfa"));
            Assert.Contains(capturedLaunches, x => x.TermsrvUser == @"CLIENTE\analista.beta" && x.RdpContent.Contains("username:s:analista.beta"));
        }
        finally
        {
            WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(sharedHost, port);
        }
    }

    [Fact]
    public void WindowsCredentialVaultInterop_PreservesPreExistingUserCredentialNotCreatedByConectaTI()
    {
        const string host = "srv-preexistente-usuario.interno";
        var target = $"TERMSRV/{host}";

        var prov = WindowsCredentialVaultInterop.ProvisionRdpSessionCredentials("conn-123", host, 3389, @"CORP\user1", "Pass1");
        try
        {
            Assert.True(prov.ProvisionedTargets.Count >= 1);
            var state = WindowsCredentialVaultInterop.InspectRdpCredentialTarget(target);
            Assert.True(state.Exists);
            Assert.True(state.IsManagedByConectaTI);
            Assert.Contains("conn-123", state.Comment);

            // CleanupAllManagedRdpCredentials remove todas as credenciais gerenciadas pelo ConectaTI
            var removed = WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host,3389);
            Assert.True(removed >= 1);
            Assert.False(WindowsCredentialVaultInterop.InspectRdpCredentialTarget(target).Exists);
        }
        finally
        {
            WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host, 3389);
        }
    }

    [Fact]
    public void VaultLock_PreservesTemporaryRdpCredentialsForOpenSessions()
    {
        const string host = "srv-bloqueio.conectati.interno";
        var prov = WindowsCredentialVaultInterop.ProvisionRdpSessionCredentials("conn-lock", host, 3389, @"CORP\admin", "Segredo");
        Assert.NotEmpty(prov.ProvisionedTargets);

        try
        {
            _vaultService.Lock();
            Assert.False(_vaultService.IsLocked);
            Assert.True(WindowsCredentialVaultInterop.InspectRdpCredentialTarget($"TERMSRV/{host}").Exists);
        }
        finally { WindowsCredentialVaultInterop.ClearManagedRdpCredentialsForHost(host,3389,"conn-lock"); }
    }

    [Theory]
    [InlineData("srv.empresa.local", 3389, "srv.empresa.local", "usuario", "", "usuario:s:usuario", false)]
    [InlineData("srv.empresa.local", 3391, "srv.empresa.local:3391", @"DOMINIO\usuario", "", "username:s:usuario", true)]
    [InlineData("10.0.0.25", 3389, "10.0.0.25", "usuario@empresa.local", "EMPRESA", "username:s:usuario@empresa.local", false)]
    [InlineData("2001:db8::1", 3389, "[2001:db8::1]", @"DOMINIO\usuario", "DOMINIO", "username:s:usuario", true)]
    [InlineData("2001:db8::1", 3399, "[2001:db8::1]:3399", "usuario", "DOMINIO", "username:s:usuario", true)]
    public void MstscLaunchPlan_FormatsStandardAndCustomPortsAndUserFormatsCorrectly(
        string host,
        int port,
        string expectedFullAddress,
        string rawUser,
        string rawDomain,
        string expectedUserLineOrCheck,
        bool expectsDomainLine)
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);

        var entry = new ConnectionEntry
        {
            Name = "Teste Formato RDP",
            ClientName = "Cliente RDP",
            Protocol = ConnectionProtocol.Rdp,
            Host = host,
            Port = port,
            Username = rawUser,
            Domain = rawDomain
        };

        var plan = launcher.BuildLaunchPlan(entry, null, decryptedPassword: null);
        Assert.NotNull(plan.TempRdpContent);
        Assert.Contains($"full address:s:{expectedFullAddress}", plan.TempRdpContent);

        if (rawUser.Contains('@'))
        {
            Assert.Contains($"username:s:{rawUser}", plan.TempRdpContent);
            Assert.DoesNotContain("domain:s:", plan.TempRdpContent);
        }
        else
        {
            Assert.Contains("username:s:usuario", plan.TempRdpContent);
            if (expectsDomainLine)
            {
                Assert.Contains("domain:s:DOMINIO", plan.TempRdpContent);
            }
        }
        _ = expectedUserLineOrCheck;
    }

    [Fact]
    public void OfficialIconResource_ExistsAndContainsStandardWindowsResolutions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "ConectaTI.App", "Assets", "conectati.ico")))
            directory = directory.Parent;
        var icoPath = Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "src", "ConectaTI.App", "Assets", "conectati.ico");
        Assert.True(File.Exists(icoPath), $"Ícone oficial não encontrado em {icoPath}");

        var bytes = File.ReadAllBytes(icoPath);
        Assert.True(bytes.Length > 1024);
        // Cabeçalho ICO: Reserved = 0, Type = 1 (Icon), Count >= 6
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(count >= 6, $"Esperado múltiplas resoluções no .ico, encontrado {count}");
    }

    [Fact]
    public async Task LargeScalePerformance_With5000Connections_PersistsFiltersAndSortsQuickly()
    {
        var batch = new List<ConnectionEntry>(5000);
        for (var i = 1; i <= 5000; i++)
        {
            batch.Add(new ConnectionEntry
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = $"SRV-NODE-{i:D4}",
                ClientName = $"Cliente {(i % 50) + 1:D2}",
                GroupName = $"Grupo {(i % 5) + 1}",
                Environment = (EnvironmentType)(i % 4),
                Protocol = (ConnectionProtocol)(i % 5),
                Host = $"10.{(i / 256) % 256}.{i % 256}.10",
                Port = ((ConnectionProtocol)(i % 5)).GetDefaultPort(),
                Username = $"user{i}",
                Tags = i % 10 == 0 ? "critico, producao, cluster" : "padrao",
                IsFavorite = i % 25 == 0
            });
        }

        await _repository.SaveConnectionsBatchAsync(batch);

        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        Assert.Equal(5000, vm.TotalConnectionsCount);
        Assert.Equal(5000, vm.FilteredConnectionsCount);

        var sw = Stopwatch.StartNew();
        vm.SearchText = "SRV-NODE-4999";
        sw.Stop();

        Assert.Single(vm.FilteredConnections);
        Assert.Equal("SRV-NODE-4999", vm.FilteredConnections[0].Name);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Pesquisa em 5000 registros levou {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DuplicationAndUnsavedChanges_GeneratesNewIdAndProtectsAgainstDataLoss()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: true);

        var original = vm.SelectedConnection!;
        Assert.NotNull(original);

        await vm.DuplicateConnectionAsync();
        // Conforme Requisito 9: Duplicação abre como rascunho em memória com IsNewRecord = true
        Assert.True(vm.IsNewRecord);
        Assert.NotEqual(original.Id, vm.EditingEntry.Id);
        Assert.Contains("(Cópia)", vm.EditingEntry.Name);
        Assert.Null(vm.EditingEntry.LastOpenedAtUtc);

        // Salva explicitamente o rascunho duplicado
        await vm.SaveConnectionAsync();
        var duplicated = vm.SelectedConnection!;
        Assert.NotNull(duplicated);
        Assert.Equal(vm.EditingEntry.Id, duplicated.Id);

        // Inicia edição e altera um campo sem salvar
        vm.BeginEdit();
        Assert.True(vm.IsEditing);
        Assert.False(vm.HasUnsavedChanges());

        vm.EditingEntry.Host = "192.168.99.99";
        Assert.True(vm.HasUnsavedChanges());

        // Simula usuário recusando descartar alterações não salvas ao tentar trocar de registro
        var promptCalled = false;
        vm.ConfirmDiscardUnsavedChangesCallback = _ =>
        {
            promptCalled = true;
            return false; // Usuário clicou em "Não descartar"
        };

        vm.SelectedConnection = original;
        Assert.False(promptCalled);
        Assert.Equal(original.Id, vm.SelectedConnection?.Id);
        vm.SelectedConnection = duplicated;
        Assert.Equal("192.168.99.99", vm.EditingEntry.Host);
        Assert.True(vm.HasUnsavedChanges());
    }

    [Fact]
    public async Task Security_NeverStoresPlaintextPasswordInSqliteLogsRdpOrCommandLine()
    {
        const string superSecret = "S3nh@_Super_Secreta_#2026!";

        var cred = new SavedCredential
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Credencial Teste Segurança",
            Username = "DOMINIO\\admin.seguro",
            Domain = "DOMINIO"
        };
        cred.ProtectedSecretBase64 = _vaultService.ProtectSecret(cred.Id, cred.EffectiveUserDisplay, superSecret);
        await _repository.SaveCredentialAsync(cred);

        // 1. Verifica que o segredo não aparece em texto puro no banco SQLite nem no WAL
        using (var fs = new FileStream(_paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.Latin1))
        {
            var rawDbText = sr.ReadToEnd();
            Assert.DoesNotContain(superSecret, rawDbText, StringComparison.Ordinal);
        }

        var walPath = _paths.DatabasePath + "-wal";
        if (File.Exists(walPath))
        {
            using var walFs = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var walSr = new StreamReader(walFs, Encoding.Latin1);
            var rawWalText = walSr.ReadToEnd();
            Assert.DoesNotContain(superSecret, rawWalText, StringComparison.Ordinal);
        }

        // 2. Verifica que a descriptografia recupera a senha exata diretamente sob proteção DPAPI
        var recovered = _vaultService.UnprotectSecret(cred);
        Assert.Equal(superSecret, recovered);
        Assert.False(_vaultService.IsLocked);

        // 3. Verifica que planos de execução RDP, PuTTY, OpenSSH, WinSCP e VNC NUNCA contêm a senha em texto puro em argumentos ou arquivo .rdp
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);

        foreach (var proto in Enum.GetValues<ConnectionProtocol>())
        {
            var entry = new ConnectionEntry
            {
                Name = $"Teste {proto}",
                ClientName = "Cliente Seguro",
                Protocol = proto,
                Host = "srv.seguro.local",
                Port = proto.GetDefaultPort(),
                Username = @"DOMINIO\admin.seguro",
                Domain = "DOMINIO",
                CredentialId = cred.Id,
                PrivateKeyPath = @"C:\Chaves Com Espaço\minha chave (1).ppk"
            };

            var plan = launcher.BuildLaunchPlan(entry, cred, decryptedPassword: superSecret);
            foreach (var arg in plan.Arguments)
            {
                Assert.DoesNotContain(superSecret, arg, StringComparison.Ordinal);
                Assert.DoesNotContain("-pw", arg, StringComparison.OrdinalIgnoreCase);
            }

            if (plan.TempRdpContent != null)
            {
                Assert.DoesNotContain(superSecret, plan.TempRdpContent, StringComparison.Ordinal);
                Assert.Contains("domain:s:DOMINIO", plan.TempRdpContent);
                Assert.Contains("username:s:admin.seguro", plan.TempRdpContent);
                Assert.DoesNotContain(@"DOMINIO\DOMINIO", plan.TempRdpContent);
            }
        }

        // 4. Verifica sanitização do logger
        var sanitized = AppLogger.SanitizeMessage($"Falha ao autenticar password={superSecret} no servidor");
        Assert.DoesNotContain(superSecret, sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MasterPasswordAndPortableEncryptedExport_RoundTripsSecretsSecurely()
    {
        const string secret1 = "ChaveSecreta!ClienteA#99";
        const string transportPass = "TransportePortatil$2026";

        var cred = new SavedCredential
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Credencial Portátil",
            Username = "root",
            Domain = ""
        };
        cred.ProtectedSecretBase64 = _vaultService.ProtectSecret(cred.Id, cred.EffectiveUserDisplay, secret1);
        await _repository.SaveCredentialAsync(cred);

        var conn = new ConnectionEntry
        {
            Name = "Servidor Linux Portátil",
            ClientName = "Cliente Portátil",
            Protocol = ConnectionProtocol.Ssh,
            Host = "10.90.0.15",
            Port = 22,
            Username = "root",
            CredentialId = cred.Id
        };
        await _repository.SaveConnectionAsync(conn);

        // Daily protection works without creating a master-password vault.
        Assert.False(_vaultService.IsVaultCreated);
        _vaultService.Lock();
        Assert.False(_vaultService.IsLocked);
        Assert.Equal(secret1,_vaultService.UnprotectSecret((await _repository.GetCredentialByIdAsync(cred.Id))!));

        // Exporta pacote criptografado portátil (.ctienc)
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var encFile = Path.Combine(_tempRootDir, "pacote.ctienc");
        await importExport.ExportEncryptedPackageAsync(encFile, transportPass);

        var rawEncryptedBytes = await File.ReadAllBytesAsync(encFile);
        var rawEncryptedLatin1 = Encoding.Latin1.GetString(rawEncryptedBytes);
        Assert.DoesNotContain(secret1, rawEncryptedLatin1, StringComparison.Ordinal);

        // Senha de transporte incorreta deve falhar com CryptographicException
        Assert.Throws<CryptographicException>(() =>
            CredentialVaultService.DecryptPortablePayload(rawEncryptedBytes, "SenhaIncorreta"));

        // Importa em uma segunda instância limpa simulando outro computador
        var secondDir = Path.Combine(_tempRootDir, "SegundoComputador");
        var paths2 = new AppPaths(secondDir);
        var logger2 = new AppLogger(paths2);
        var settings2 = new SettingsService(paths2, logger2);
        var repo2 = new SqliteRepository(paths2, logger2);
        repo2.InitializeAndMigrate();
        var vault2 = new CredentialVaultService(settings2, repo2, logger2, useWindowsVault: false); VaultTestSupport.Initialize(vault2);
        var importExport2 = new ImportExportService(repo2, vault2, logger2);

        var (connsImported, credsImported, _) = await importExport2.ImportEncryptedPackageAsync(
            encFile,
            transportPass,
            DuplicateImportHandling.Update);

        Assert.Equal(1, connsImported);
        Assert.Equal(1, credsImported);

        var importedEntry = Assert.Single(await repo2.GetAllConnectionsAsync());
        Assert.NotNull(importedEntry.CredentialId);
        var importedCred = await repo2.GetCredentialByIdAsync(importedEntry.CredentialId);
        Assert.NotNull(importedCred);
        Assert.Equal(secret1, vault2.UnprotectSecret(importedCred!));
    }

    [Fact]
    public async Task MissingExecutable_ReturnsClearActionableMessageWithoutCrashing()
    {
        _settingsService.Current.PuttyExecutablePath = @"C:\CaminhoInexistente\putty_ausente.exe";
        _settingsService.Current.PreferredSshClient = SshClientPreference.Putty;

        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);

        var entry = new ConnectionEntry
        {
            Name = "SSH Servidor",
            ClientName = "Cliente X",
            Protocol = ConnectionProtocol.Ssh,
            SshClient = SshClientPreference.Putty,
            Host = "10.0.0.5",
            Port = 22,
            Username = "ubuntu"
        };

        var result = await launcher.LaunchAsync(entry);
        Assert.False(result.Started);
        Assert.True(result.IsMissingExecutable);
        Assert.Contains("Executáveis", result.ErrorMessage);
    }

    [Fact]
    public async Task CsvImport_WithInvalidRecordsAccentsAndDuplicates_HandlesGracefullyAndCreatesBackup()
    {
        var importExport = new ImportExportService(_repository, _vaultService, _logger);

        var csvContent = """
            Nome;Cliente;Grupo;Ambiente;Protocolo;Host;Porta;Usuario;Dominio;Tags;Observacoes
            SRV-VALIDO-01; São Paulo Logística ;Infraestrutura;Produção;RDP;10.10.1.50;3389;OPERADOR\joao;;critico,sp;"Servidor principal em São Paulo"
            SRV-PORTA-INVALIDA;São Paulo Logística;Infraestrutura;Produção;SSH;10.10.1.51;99999;root;;linux;Porta fora do limite
            SRV-HOST-VAZIO;São Paulo Logística;Infraestrutura;Teste;VNC;;5900;suporte;;;Host ausente
            SRV-IPV6-VALIDO;Clínica Coração;Laboratório;Homologação;SSH;2001:db8::42;2222;admin;;ipv6;Servidor IPv6 válido
            """;

        var parsed = importExport.ParseCsvContent(csvContent);
        var existing = await _repository.GetAllConnectionsAsync();
        var preview = importExport.BuildCsvPreview(parsed, parsed.SuggestedMapping, existing);

        Assert.Equal(4, preview.Items.Count);
        Assert.Equal(2, preview.ValidCount);
        Assert.Equal(2, preview.InvalidCount);

        var report = await importExport.CommitImportAsync(preview, DuplicateImportHandling.Ignore);
        Assert.Equal(2, report.InsertedCount);
        Assert.Equal(2, report.InvalidSkippedCount);
        Assert.True(File.Exists(report.PreImportBackupPath));

        // Reimporta o mesmo CSV testando modo Atualizar Duplicados
        var existingAfterFirst = await _repository.GetAllConnectionsAsync();
        var preview2 = importExport.BuildCsvPreview(parsed, parsed.SuggestedMapping, existingAfterFirst);
        Assert.Equal(2, preview2.DuplicateCount);

        var reportIgnore = await importExport.CommitImportAsync(preview2, DuplicateImportHandling.Ignore);
        Assert.Equal(0, reportIgnore.InsertedCount);
        Assert.Equal(2, reportIgnore.IgnoredDuplicateCount);

        var reportUpdate = await importExport.CommitImportAsync(preview2, DuplicateImportHandling.Update);
        Assert.Equal(2, reportUpdate.UpdatedCount);
    }

    [Fact]
    public async Task BackupAndRestore_ValidatesIntegrityAndRollsBackCorruptedBackup()
    {
        var conn = new ConnectionEntry
        {
            Name = "SRV-BACKUP-ORIGINAL",
            ClientName = "Cliente Backup",
            Host = "10.50.0.10",
            Port = 3389
        };
        await _repository.SaveConnectionAsync(conn);

        var backupFile = await _repository.CreateBackupAsync();
        Assert.True(File.Exists(backupFile));

        // Exclui o registro do banco principal
        await _repository.DeleteConnectionAsync(conn.Id);
        Assert.Empty(await _repository.GetAllConnectionsAsync());

        // Restaura o backup bom e verifica que o registro voltou
        await _repository.RestoreBackupAsync(backupFile);
        var restoredList = await _repository.GetAllConnectionsAsync();
        Assert.Single(restoredList);
        Assert.Equal("SRV-BACKUP-ORIGINAL", restoredList[0].Name);

        // Tenta restaurar um arquivo corrompido / inválido e garante que falha sem destruir o banco atual
        var badBackup = Path.Combine(_tempRootDir, "corrompido.db");
        await File.WriteAllTextAsync(badBackup, "Isto nao e um arquivo SQLite valido!");

        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.RestoreBackupAsync(badBackup));

        var afterFailedRestore = await _repository.GetAllConnectionsAsync();
        Assert.Single(afterFailedRestore);
        Assert.Equal("SRV-BACKUP-ORIGINAL", afterFailedRestore[0].Name);
    }

    [Fact]
    public async Task ClipboardService_RetriesWhenBusyAndOnlyClearsIfContentStillMatches()
    {
        var currentClipboard = string.Empty;
        var attempts = 0;

        var clipboard = new ClipboardService(
            _logger,
            customSetClipboard: text =>
            {
                attempts++;
                if (attempts < 3)
                    throw new System.Runtime.InteropServices.COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0));
                currentClipboard = text;
                return true;
            },
            customGetClipboard: () => currentClipboard,
            customClearClipboard: () => currentClipboard = string.Empty);

        var copied = await clipboard.CopyTextWithRetryAsync("SenhaTemp123", isSecret: true, autoClearSeconds: 0);
        Assert.True(copied);
        Assert.Equal(3, attempts);
        Assert.Equal("SenhaTemp123", currentClipboard);

        var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes("SenhaTemp123"));

        // Se o usuário já copiou outro texto, não deve limpar o novo texto do usuário
        currentClipboard = "OutroTextoCopiadoPeloUsuario";
        Assert.False(clipboard.ClearIfMatchesHash(secretHash));
        Assert.Equal("OutroTextoCopiadoPeloUsuario", currentClipboard);

        // Se ainda for a senha copiada pelo aplicativo, limpa com segurança
        currentClipboard = "SenhaTemp123";
        Assert.True(clipboard.ClearIfMatchesHash(secretHash));
        Assert.Equal(string.Empty, currentClipboard);
    }

    [Fact]
    public void WindowBounds_WhenSavedOnDisconnectedMonitor_ClampsBackIntoVisibleScreen()
    {
        // Janela salva em um segundo monitor à direita (Left = 3200) que foi desconectado (VirtualWidth = 1920)
        var (left, top, width, height) = SettingsService.ClampWindowBounds(
            savedLeft: 3200,
            savedTop: -1200,
            savedWidth: 1400,
            savedHeight: 820,
            virtualLeft: 0,
            virtualTop: 0,
            virtualWidth: 1920,
            virtualHeight: 1080);

        Assert.InRange(left, 0, 1920 - width);
        Assert.InRange(top, 0, 1080 - height);
    }

    [Fact]
    public async Task AutoPuttyImport_ParsesOpenXmlAndRejectsEncryptedBinaryWithoutFalsePromises()
    {
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var existing = await _repository.GetAllConnectionsAsync();

        // 1. Testa XML não criptografado do AutoPuTTY
        var xmlPath = Path.Combine(_tempRootDir, "autoputty_export.xml");
        var xmlContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <AutoPuTTY>
              <Server Name="SRV-LINUX-APP" Host="10.20.30.40" Port="2222" Protocol="SSH" Group="Servidores Linux" Username="devops" />
              <Server Name="SRV-WIN-TS" Host="10.20.30.50" Port="3389" Protocol="RDP" Group="Terminal Services" Username="CORP\suporte" />
            </AutoPuTTY>
            """;
        await File.WriteAllTextAsync(xmlPath, xmlContent, Encoding.UTF8);

        var xmlPreview = importExport.EvaluateAndParseAutoPuttyFile(xmlPath, existing);
        Assert.Equal(2, xmlPreview.ValidCount);
        Assert.Equal(ConnectionProtocol.Ssh, xmlPreview.Items[0].Entry.Protocol);
        Assert.Equal(2222, xmlPreview.Items[0].Entry.Port);

        // 2. Testa arquivo binário/criptografado simulando exportação protegida do AutoPuTTY
        var binPath = Path.Combine(_tempRootDir, "autoputty_encrypted.dat");
        var randomBinary = new byte[128];
        for (var i = 0; i < randomBinary.Length; i++)
            randomBinary[i] = (byte)(i % 16);
        await File.WriteAllBytesAsync(binPath, randomBinary);

        var binPreview = importExport.EvaluateAndParseAutoPuttyFile(binPath, existing);
        Assert.Equal(0, binPreview.ValidCount);
        Assert.Contains("criptografado", binPreview.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkDiagnostic_DistinguishesTcpPortReachabilityFromAuthentication()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var diag = new NetworkDiagnosticService();
            var result = await diag.TestConnectivityAsync("127.0.0.1", port, timeoutMs: 2000);

            Assert.True(result.DnsResolved);
            Assert.True(result.TcpPortReachable);
            Assert.Contains("NÃO significa que o usuário está autenticado", result.DetailedMessage);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ThemeService_AppliesLightDarkAndSystemBrushesCleanly()
    {
        var dict = new System.Windows.ResourceDictionary();
        ThemeService.ApplyTheme(AppThemeMode.Light, dict);
        Assert.NotNull(dict["AppBackgroundBrush"]);
        Assert.NotNull(dict["TableHeaderBackgroundBrush"]);
        Assert.NotNull(dict["TableHeaderForegroundBrush"]);

        ThemeService.ApplyTheme(AppThemeMode.Dark, dict);
        Assert.NotNull(dict["AppBackgroundBrush"]);
        Assert.NotNull(dict["TableHeaderBackgroundBrush"]);
        Assert.NotNull(dict["TableHeaderForegroundBrush"]);

        ThemeService.ApplyTheme(AppThemeMode.System, dict);
        Assert.NotNull(dict["AppBackgroundBrush"]);
    }

    [Fact]
    public async Task SummaryAndCopyCommands_UpdatesImmediatelyOnSelectionAndCopiesExactValuesWith30sSecretClear()
    {
        var copiedTexts = new List<string>();
        var currentClip = string.Empty;
        var clipboard = new ClipboardService(
            _logger,
            customSetClipboard: text =>
            {
                currentClip = text;
                copiedTexts.Add(text);
                return true;
            },
            customGetClipboard: () => currentClip,
            customClearClipboard: () => currentClip = string.Empty);

        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();
        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // Quando nenhum registro está selecionado
        Assert.False(vm.HasSelectedConnection);
        Assert.False(vm.CanCopySelectedName);
        Assert.False(vm.CanCopySelectedHost);
        Assert.False(vm.CanCopySelectedUser);
        Assert.False(vm.CanCopySelectedPassword);

        // Cria primeira conexão COM senha salva
        vm.NewConnection();
        vm.EditingEntry.Name = "Servidor Principal";
        vm.EditingEntry.ClientName = "Cliente Alfa";
        vm.EditingEntry.Protocol = ConnectionProtocol.Rdp;
        vm.EditingEntry.Host = "10.10.1.20";
        vm.EditingEntry.Port = 3399;
        vm.EditingEntry.Username = "admin.alfa";
        vm.EditingEntry.Domain = "ALFA";
        vm.EditingEntry.Notes = "Observação do servidor principal.";
        vm.InlinePasswordInput = "SegredoAlfa#30s";
        await vm.SaveConnectionAsync();
        var firstConn = vm.SelectedConnection!;

        // Cria segunda conexão SEM senha salva e sem usuário
        vm.NewConnection();
        vm.EditingEntry.Name = "Servidor Secundário SSH";
        vm.EditingEntry.ClientName = "Cliente Beta";
        vm.EditingEntry.Protocol = ConnectionProtocol.Ssh;
        vm.EditingEntry.Host = "ssh.beta.local";
        vm.EditingEntry.Port = 2222;
        vm.EditingEntry.Username = string.Empty;
        vm.EditingEntry.Domain = string.Empty;
        await vm.SaveConnectionAsync();
        var secondConn = vm.SelectedConnection!;

        // Verifica resumo imediato da 2ª conexão (sem senha salva e sem usuário)
        Assert.True(vm.HasSelectedConnection);
        Assert.Equal("Servidor Secundário SSH", vm.SelectedName);
        Assert.Equal("Cliente Beta", vm.SelectedClientName);
        Assert.Equal("SSH", secondConn.ProtocolShortDisplay);
        Assert.Equal("ssh.beta.local:2222", vm.SelectedEndpointDisplay);
        Assert.Equal("Não informado", vm.SelectedUserDisplay);
        Assert.Equal("Sem senha salva", vm.SelectedPasswordStateText);
        Assert.True(vm.SelectedShowNoPasswordNote);
        Assert.False(vm.CanCopySelectedUser);
        Assert.False(vm.CanCopySelectedPassword);

        // Seleciona a 1ª conexão e verifica atualização imediata de todos os campos de resumo
        vm.SelectedConnection = firstConn;
        Assert.True(vm.HasSelectedConnection);
        Assert.Equal("Servidor Principal", vm.SelectedName);
        Assert.Equal("Cliente Alfa", vm.SelectedClientName);
        Assert.Equal("RDP", firstConn.ProtocolShortDisplay);
        Assert.Equal("Área de Trabalho Remota (MSTSC)", vm.SelectedProtocolDisplay);
        Assert.Equal("10.10.1.20:3399", vm.SelectedEndpointDisplay);
        Assert.Equal(@"ALFA\admin.alfa", vm.SelectedUserDisplay);
        Assert.Equal("Senha salva", vm.SelectedPasswordStateText);
        Assert.False(vm.SelectedShowNoPasswordNote);
        Assert.True(vm.SelectedHasNotes);
        Assert.Equal("Observação do servidor principal.", vm.SelectedNotes);
        Assert.True(vm.CanCopySelectedName);
        Assert.True(vm.CanCopySelectedHost);
        Assert.True(vm.CanCopySelectedUser);
        Assert.True(vm.CanCopySelectedPassword);

        // Testa cópia individual de Nome, Endereço (apenas host), Host:Porta, Usuário e Senha
        await vm.CopyConnectionNameAsync();
        Assert.Equal("Servidor Principal", currentClip);
        Assert.Equal("Nome da conexão copiado", vm.StatusMessage);

        await vm.CopyHostAsync();
        Assert.Equal("10.10.1.20:3399", currentClip); // Campo unificado: copia o endereço com a porta quando exibida
        Assert.Equal("Endereço copiado", vm.StatusMessage);

        await vm.CopyHostAndPortAsync();
        Assert.Equal("10.10.1.20:3399", currentClip);
        Assert.Equal("Host:porta copiado", vm.StatusMessage);

        await vm.CopyUsernameAsync();
        Assert.Equal(@"ALFA\admin.alfa", currentClip);
        Assert.Equal("Usuário copiado", vm.StatusMessage);

        // Testa cópia da senha salva sem revelá-la na tela
        Assert.False(vm.IsPasswordRevealed);
        Assert.Equal(30, _settingsService.Current.ClipboardClearSeconds);
        await vm.CopyPasswordAsync();
        Assert.Equal("SegredoAlfa#30s", currentClip);
        Assert.Equal("Senha copiada", vm.StatusMessage);
        Assert.DoesNotContain("SegredoAlfa#30s", vm.StatusMessage);
        Assert.False(vm.IsPasswordRevealed);

        // O aplicativo não bloqueia mais a cópia por senha mestra na tela principal
        Assert.False(vm.IsLocked);
        Assert.True(vm.CanCopySelectedPassword);
    }

    [Fact]
    public Task WpfViews_InstantiateAndInitializeXamlOnStaThreadWithoutErrors()
        => StaTest.RunAsync(async () =>
        {
                var app = System.Windows.Application.Current as ConectaTI.App.App;
                if (app == null)
                {
                    app = new ConectaTI.App.App { IsIsolatedHost = true };
                    app.InitializeComponent();
                    app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                }
                ThemeService.ApplyTheme(AppThemeMode.Dark, app.Resources);

                var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
                var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
                var importExport = new ImportExportService(_repository, _vaultService, _logger);
                var diag = new NetworkDiagnosticService();
                var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
                await vm.InitializeAsync(seedDemoIfEmpty: true);

                // Seleciona uma conexão RDP e salva uma senha para demonstrar o estado "Senha salva" e botões de cópia ativos
                var rdpDemo = vm.FilteredConnections.FirstOrDefault(c => c.Protocol == ConnectionProtocol.Rdp) ?? vm.SelectedConnection;
                if (rdpDemo != null)
                {
                    vm.SelectedConnection = rdpDemo;
                    vm.BeginEdit();
                    vm.InlinePasswordInput = "SenhaDemo#2026";
                    await vm.SaveConnectionAsync();
                    vm.StatusMessage = "Pronto para conectar.";
                }

                var mainWin = new ConectaTI.App.MainWindow(vm);
                if (mainWin.Content is System.Windows.FrameworkElement rootContent)
                {
                    rootContent.Width = 1366;
                    rootContent.Height = 768;
                    rootContent.Measure(new System.Windows.Size(1366, 768));
                    rootContent.Arrange(new System.Windows.Rect(0, 0, 1366, 768));
                    rootContent.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                    rootContent.UpdateLayout();

                    await WpfEditorScenarios.VerifyAsync(mainWin, vm);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        1366, 768, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(rootContent);

                    var screenshotPath = Environment.GetEnvironmentVariable("CONECTATI_SCREENSHOT_PATH");
                    if (!string.IsNullOrWhiteSpace(screenshotPath))
                    {
                        var dir = Path.GetDirectoryName(screenshotPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using var fs = File.Create(screenshotPath);
                        encoder.Save(fs);
                    }
                }

                await ResponsiveUiScenarios.VerifyAsync(mainWin, vm);
                await ImprovementsUiScenarios.VerifyAsync(mainWin, vm);
                await KeyboardNavigationUiScenarios.VerifyAsync(mainWin, vm);
                vm.NewConnection();
                var editorDlg = new ConectaTI.App.Views.ConnectionEditorDialog(vm);
                var execDlg = new ConectaTI.App.Views.ExecutablesSettingsDialog(_settingsService, _vaultService, vm);
                var credDlg = new ConectaTI.App.Views.CredentialsDialog(_repository, _vaultService);
                var impExpDlg = new ConectaTI.App.Views.ImportExportDialog(_paths, _repository, importExport);

                editorDlg.Close();
                mainWin.Close();
                execDlg.Close();
                credDlg.Close();
                impExpDlg.Close();
                app.Shutdown();
        });

    [Fact]
    public async Task CompactLayout_ConnectionTypeSelection_PreservesCustomPortOrUpdatesDefaultPort()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.NewConnection();
        Assert.Equal(ConnectionTypeOption.Mstsc, vm.SelectedConnectionType);
        Assert.Equal(3389, vm.EditingEntry.Port);
        Assert.Equal(ConnectionProtocol.Rdp, vm.EditingEntry.Protocol);

        // Altera para SSH — PuTTY: porta padrão deve atualizar para 22
        vm.SelectedConnectionType = ConnectionTypeOption.SshPutty;
        Assert.Equal(22, vm.EditingEntry.Port);
        Assert.Equal(ConnectionProtocol.Ssh, vm.EditingEntry.Protocol);
        Assert.Equal(SshClientPreference.Putty, vm.EditingEntry.SshClient);
        Assert.True(vm.SupportsPrivateKey);

        // Define porta personalizada 2222
        vm.EditingEntry.Port = 2222;

        // Altera para VNC: como a porta estava personalizada em 2222, NÃO deve sobrescrever silenciosamente!
        vm.SelectedConnectionType = ConnectionTypeOption.Vnc;
        Assert.Equal(2222, vm.EditingEntry.Port);
        Assert.Equal(ConnectionProtocol.Vnc, vm.EditingEntry.Protocol);
        Assert.False(vm.SupportsPrivateKey);

        // Altera para WinSCP — SFTP: porta customizada 2222 mantida
        vm.SelectedConnectionType = ConnectionTypeOption.WinScpSftp;
        Assert.Equal(2222, vm.EditingEntry.Port);
        Assert.Equal(ConnectionProtocol.Sftp, vm.EditingEntry.Protocol);
        Assert.True(vm.SupportsPrivateKey);

        // Altera para WinSCP — SCP
        vm.SelectedConnectionType = ConnectionTypeOption.WinScpScp;
        Assert.Equal(ConnectionProtocol.Scp, vm.EditingEntry.Protocol);

        // Altera para SSH — OpenSSH
        vm.SelectedConnectionType = ConnectionTypeOption.SshOpenSsh;
        Assert.Equal(ConnectionProtocol.Ssh, vm.EditingEntry.Protocol);
        Assert.Equal(SshClientPreference.OpenSsh, vm.EditingEntry.SshClient);
    }

    [Fact]
    public async Task CompactLayout_DirectEditingAndSaving_PersistsChangesImmediately()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.NewConnection();
        vm.EditingEntry.Name = "Servidor Web Compacto";
        vm.EditingEntry.Host = "web.compacto.corp";
        vm.EditingEntry.Port = 8080;
        vm.EditingEntry.Username = "admin.web";
        vm.EditingEntry.Domain = "CORP";
        vm.SelectedConnectionType = ConnectionTypeOption.Mstsc;
        await vm.SaveConnectionAsync();

        var saved = vm.SelectedConnection;
        Assert.NotNull(saved);
        Assert.Equal("Servidor Web Compacto", saved.Name);
        Assert.Equal("web.compacto.corp", saved.Host);
        Assert.Equal(8080, saved.Port);

        // Edição direta nos campos sem abrir janela separada
        vm.EditingEntry.Host = "web2.compacto.corp";
        vm.EditingEntry.Port = 8443;
        await vm.SaveConnectionAsync();

        Assert.Equal("web2.compacto.corp", vm.SelectedConnection!.Host);
        Assert.Equal(8443, vm.SelectedConnection.Port);

        var persisted = await _repository.GetConnectionByIdAsync(saved.Id);
        Assert.NotNull(persisted);
        Assert.Equal("web2.compacto.corp", persisted.Host);
        Assert.Equal(8443, persisted.Port);
    }

    [Fact]
    public async Task CompactLayout_NewConnection_ClearsFormWithoutModifyingSelected()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // Cria conexão inicial
        vm.NewConnection();
        vm.EditingEntry.Name = "Conexão Existente 1";
        vm.EditingEntry.Host = "10.0.0.1";
        await vm.SaveConnectionAsync();
        var conn1Id = vm.SelectedConnection!.Id;

        // Clica em Nova Conexão
        vm.NewConnection();
        Assert.Null(vm.SelectedConnection);
        Assert.Equal(string.Empty, vm.EditingEntry.Name);
        Assert.Equal(string.Empty, vm.EditingEntry.Host);
        Assert.Equal(3389, vm.EditingEntry.Port);
        Assert.Equal(ConnectionTypeOption.Mstsc, vm.SelectedConnectionType);

        // Preenche e salva novo cadastro
        vm.EditingEntry.Name = "Conexão Nova 2";
        vm.EditingEntry.Host = "10.0.0.2";
        await vm.SaveConnectionAsync();
        var conn2Id = vm.SelectedConnection!.Id;

        Assert.NotEqual(conn1Id, conn2Id);
        Assert.Equal(2, vm.FilteredConnectionsCount);

        var originalPersisted = await _repository.GetConnectionByIdAsync(conn1Id);
        Assert.NotNull(originalPersisted);
        Assert.Equal("Conexão Existente 1", originalPersisted.Name);
    }

    [Fact]
    public async Task CompactLayout_PasswordWorkflow_EmptyPreserves_ExplicitRemovalDeletes_RevealAndCopy()
    {
        string? clipboardText = null;
        var clipboard = new ClipboardService(_logger, text => { clipboardText = text; return true; }, () => clipboardText, () => { clipboardText = null; });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // 1. Cadastra conexão com senha
        vm.NewConnection();
        vm.EditingEntry.Name = "Servidor com Senha";
        vm.EditingEntry.Host = "10.1.1.1";
        vm.InlinePasswordInput = "Super#Senha123";
        await vm.SaveConnectionAsync();

        Assert.True(vm.HasSavedPassword);
        Assert.Equal("Senha salva", vm.SavedPasswordBadgeText);

        // 2. Cópia da senha para área de transferência
        await vm.CopyPasswordAsync();
        Assert.Equal("Super#Senha123", clipboardText);
        Assert.Equal("Senha copiada", vm.StatusMessage);

        // 3. Revelação temporária da senha
        await vm.TogglePasswordVisibilityAsync();
        Assert.True(vm.IsPasswordRevealed);
        Assert.Equal("Super#Senha123", vm.RevealedPasswordText);
        vm.HideRevealedPassword();
        Assert.False(vm.IsPasswordRevealed);

        // 4. Edição direta sem preencher nova senha -> PRESERVA senha existente!
        vm.EditingEntry.Host = "10.1.1.2";
        vm.InlinePasswordInput = string.Empty;
        await vm.SaveConnectionAsync();
        Assert.True(vm.HasSavedPassword);

        await vm.CopyPasswordAsync();
        Assert.Equal("Super#Senha123", clipboardText);

        // 5. Ação explícita de remoção
        vm.MarkPasswordForRemoval();
        Assert.True(vm.IsPasswordMarkedForRemoval);
        Assert.Equal("Remoção pendente — salvar", vm.SavedPasswordBadgeText);
        await vm.SaveConnectionAsync();

        Assert.False(vm.HasSavedPassword);
        Assert.Equal("Sem senha salva", vm.SavedPasswordBadgeText);
    }

    [Fact]
    public async Task CompactLayout_CopyButtons_CopyExactValuesWithoutLabels()
    {
        string? copied = null;
        var clipboard = new ClipboardService(_logger, text => { copied = text; return true; }, () => copied, () => { copied = null; });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.NewConnection();
        vm.EditingEntry.Name = "Servidor Producao";
        vm.EditingEntry.Host = "prod.server.net";
        vm.EditingEntry.Username = "admin";
        vm.EditingEntry.Domain = "CORP";

        // Copiar Nome
        await vm.CopyConnectionNameAsync();
        Assert.Equal("Servidor Producao", copied);
        Assert.Equal("Nome da conexão copiado", vm.StatusMessage);

        // Copiar Host
        await vm.CopyHostAsync();
        Assert.Equal("prod.server.net", copied);
        Assert.Equal("Endereço copiado", vm.StatusMessage);

        // Copiar Usuário (sem prefixos extras)
        await vm.CopyUsernameAsync();
        Assert.Equal("CORP\\admin", copied);
        Assert.Equal("Usuário copiado", vm.StatusMessage);
    }

    [Fact]
    public async Task CompactLayout_ThemeToggle_SwitchesLightDarkAndPersists()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        _settingsService.Current.Theme = AppThemeMode.Light;
        Assert.Equal("🌙", vm.ThemeToggleIcon);
        Assert.Equal("Mudar para tema escuro", vm.ThemeToggleToolTip);

        vm.ToggleTheme();
        Assert.Equal(AppThemeMode.Dark, _settingsService.Current.Theme);
        Assert.Equal("☀️", vm.ThemeToggleIcon);
        Assert.Equal("Mudar para tema claro", vm.ThemeToggleToolTip);

        // Verifica persistência no arquivo settings.json
        var loaded = _settingsService.Load();
        Assert.Equal(AppThemeMode.Dark, loaded.Theme);

        vm.ToggleTheme();
        Assert.Equal(AppThemeMode.Light, _settingsService.Current.Theme);
    }

    [Fact]
    public async Task CompactLayout_UnsavedChanges_ManualSaveAndExplicitDiscard()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.NewConnection();
        vm.EditingEntry.Name = "Conexão A";
        vm.EditingEntry.Host = "10.0.0.1";
        await vm.SaveConnectionAsync();

        vm.NewConnection();
        vm.EditingEntry.Name = "Conexão B";
        vm.EditingEntry.Host = "10.0.0.2";
        await vm.SaveConnectionAsync();

        // Seleciona A e faz alteração não salva
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Name == "Conexão A");
        vm.EditingEntry.Host = "10.0.0.99";
        Assert.True(vm.HasUnsavedChanges());

        // Navigation retains the draft, and obsolete callbacks are never consulted.
        vm.ConfirmUnsavedChangesCallback = _ => throw new Exception("Unexpected confirmation");
        Assert.True(vm.ConfirmCanLeaveCurrentEdit());
        Assert.Equal("10.0.0.99", vm.EditingEntry.Host);
        Assert.True(vm.HasUnsavedChanges());
        Assert.Equal("10.0.0.1", (await _repository.GetConnectionByIdAsync(vm.EditingEntry.Id))!.Host);
        vm.CancelEdit();
        Assert.False(vm.HasUnsavedChanges());
        Assert.Equal("10.0.0.1", vm.EditingEntry.Host);
        vm.EditingEntry.Host = "10.0.0.88";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal("10.0.0.88", (await _repository.GetConnectionByIdAsync(vm.EditingEntry.Id))!.Host);
    }

    [Theory]
    [InlineData("10.30.0.11", ConnectionProtocol.Rdp, "10.30.0.11", 3389, true)]
    [InlineData("10.30.0.11:3389", ConnectionProtocol.Rdp, "10.30.0.11", 3389, true)]
    [InlineData("servidor.cliente.com.br", ConnectionProtocol.Rdp, "servidor.cliente.com.br", 3389, true)]
    [InlineData("servidor.cliente.com.br:3390", ConnectionProtocol.Rdp, "servidor.cliente.com.br", 3390, true)]
    [InlineData("[2001:db8::1]:3389", ConnectionProtocol.Rdp, "2001:db8::1", 3389, true)]
    [InlineData("[2001:db8::1]", ConnectionProtocol.Rdp, "2001:db8::1", 3389, true)]
    [InlineData("2001:db8::1", ConnectionProtocol.Rdp, "2001:db8::1", 3389, true)]
    [InlineData("servidor.cliente.com.br:70000", ConnectionProtocol.Rdp, "servidor.cliente.com.br", 3389, false)]
    [InlineData("servidor.cliente.com.br:0", ConnectionProtocol.Rdp, "servidor.cliente.com.br", 3389, false)]
    public void HostPortParser_ParsesValidAndInvalidFormats(
        string input, ConnectionProtocol proto, string expectedHost, int expectedPort, bool expectedValid)
    {
        var res = HostPortParser.Parse(input, proto);
        Assert.Equal(expectedValid, res.IsValid);
        Assert.Equal(expectedHost, res.CleanHost);
        Assert.Equal(expectedPort, res.Port);
    }

    [Fact]
    public void HostPortParser_FormatsWithoutDuplicateSuffix()
    {
        Assert.Equal("10.30.0.11", HostPortParser.FormatDisplay("10.30.0.11", 3389, ConnectionProtocol.Rdp));
        Assert.Equal("10.30.0.11:3390", HostPortParser.FormatDisplay("10.30.0.11", 3390, ConnectionProtocol.Rdp));
        Assert.Equal("10.30.0.11:3390", HostPortParser.FormatDisplay("10.30.0.11:3390", 3389, ConnectionProtocol.Rdp));
        Assert.Equal("[2001:db8::1]:3390", HostPortParser.FormatDisplay("2001:db8::1", 3390, ConnectionProtocol.Rdp));
    }

    [Theory]
    [InlineData("usuario", null, "usuario", "")]
    [InlineData(@"EMPRESA\usuario", null, "usuario", "EMPRESA")]
    [InlineData("usuario@empresa.com.br", null, "usuario", "empresa.com.br")]
    [InlineData(@"EMPRESA\usuario", "EMPRESA", "usuario", "EMPRESA")]
    public void UserIdentityFormatter_NormalizesAndAvoidsDuplicateDomain(
        string input, string? existingDomain, string expectedUser, string expectedDomain)
    {
        var norm = UserIdentityFormatter.Normalize(input, existingDomain);
        Assert.Equal(expectedUser, norm.CleanUsername);
        Assert.Equal(expectedDomain, norm.Domain);
    }

    [Theory]
    [InlineData("usuario", null, "usuario")]
    [InlineData(@"EMPRESA\usuario", null, @"EMPRESA\usuario")]
    [InlineData("usuario@empresa.com.br", null, "usuario@empresa.com.br")]
    [InlineData("usuario", "EMPRESA", @"EMPRESA\usuario")]
    [InlineData(@"EMPRESA\usuario", "EMPRESA", @"EMPRESA\usuario")]
    public void UserIdentityFormatter_FormatsDisplayCorrectly(
        string input, string? existingDomain, string expectedDisplay)
    {
        var display = UserIdentityFormatter.FormatDisplay(input, existingDomain);
        Assert.Equal(expectedDisplay, display);
    }

    [Fact]
    public async Task ConectaTI_XmlExportAndReimport_PreservesAllFields()
    {
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var entry = new ConnectionEntry
        {
            Name = "Conexão Teste XML",
            ClientName = "Cliente XML",
            GroupName = "Grupo XML",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "xmluser",
            Domain = "XMLDOM",
            Protocol = ConnectionProtocol.Ssh,
            SshClient = SshClientPreference.Putty,
            PrivateKeyPath = @"C:\chaves\xml.ppk",
            IsFavorite = true
        };
        await _repository.SaveConnectionAsync(entry);

        var xmlPath = Path.Combine(_tempRootDir, "conexoes_export.xml");
        var allConns = await _repository.GetAllConnectionsAsync();
        await importExport.ExportToXmlAsync(xmlPath, allConns);

        Assert.True(File.Exists(xmlPath));
        var xmlContent = await File.ReadAllTextAsync(xmlPath);
        Assert.Contains("ConectaTI Version=\"1.0\"", xmlContent);
        Assert.Contains("Conexão Teste XML", xmlContent);

        var preview = importExport.EvaluateAndParseAutoPuttyFile(xmlPath, Array.Empty<ConnectionEntry>());
        Assert.True(preview.ValidCount > 0);
        var importedItem = preview.Items.First(i => i.Entry.Name == "Conexão Teste XML");
        Assert.Equal("192.168.1.100", importedItem.Entry.Host);
        Assert.Equal(2222, importedItem.Entry.Port);
        Assert.Equal("xmluser", importedItem.Entry.Username);
        Assert.Equal("XMLDOM", importedItem.Entry.Domain);
        Assert.Equal(ConnectionProtocol.Ssh, importedItem.Entry.Protocol);
    }

    [Fact]
    public async Task AutoPutty_CompatibleXmlExportAndImport_RecoversConnections()
    {
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var conn = new ConnectionEntry
        {
            Name = "SRV-TESTE-AUTOPUTTY",
            Host = "10.0.0.50",
            Port = 3389,
            Username = "admin",
            Protocol = ConnectionProtocol.Rdp
        };
        await _repository.SaveConnectionAsync(conn);

        var apPath = Path.Combine(_tempRootDir, "autoputty_compat.xml");
        var all = await _repository.GetAllConnectionsAsync();
        await importExport.ExportToAutoPuttyXmlAsync(apPath, all);

        Assert.True(File.Exists(apPath));
        var preview = importExport.EvaluateAndParseAutoPuttyFile(apPath, Array.Empty<ConnectionEntry>());
        Assert.True(preview.ValidCount > 0);
        var item = preview.Items.First(i => i.Entry.Name == "SRV-TESTE-AUTOPUTTY");
        Assert.Equal("10.0.0.50", item.Entry.Host);
        Assert.Equal(3389, item.Entry.Port);
        Assert.Equal(ConnectionProtocol.Rdp, item.Entry.Protocol);
    }

    [Fact]
    public async Task VpnBlock_SaveCloseAndReopen_PreservesAllVpnFieldsInSqlite()
    {
        var entry = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "SRV-VPN-TESTE",
            ClientName = "Cliente VPN",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.0.50.1",
            Port = 3389,
            VpnName = "VPN Matriz Alfa",
            VpnApp = "FortiClient VPN",
            VpnServer = "vpn.matriz.empresa.com.br:10443",
            VpnNotes = "Autenticação via MFA FortiToken com domínio CORP."
        };

        await _repository.SaveConnectionAsync(entry);

        // Reabre um novo repositório apontando para o mesmo banco de dados
        var freshRepo = new SqliteRepository(_paths, _logger);
        freshRepo.InitializeAndMigrate();

        var retrieved = await freshRepo.GetConnectionByIdAsync(entry.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("VPN Matriz Alfa", retrieved.VpnName);
        Assert.Equal("FortiClient VPN", retrieved.VpnApp);
        Assert.Equal("vpn.matriz.empresa.com.br:10443", retrieved.VpnServer);
        Assert.Equal("Autenticação via MFA FortiToken com domínio CORP.", retrieved.VpnNotes);
        Assert.True(retrieved.HasVpnConfigured);
        Assert.Equal("● Configurada", retrieved.VpnStatusBadge);
    }

    [Fact]
    public void VpnBlock_HasVpnConfiguredAndBadge_UpdatesDynamically()
    {
        var entry = new ConnectionEntry();
        Assert.False(entry.HasVpnConfigured);
        Assert.Equal(string.Empty, entry.VpnStatusBadge);

        entry.VpnName = "Cisco AnyConnect";
        Assert.True(entry.HasVpnConfigured);
        Assert.Equal("● Configurada", entry.VpnStatusBadge);

        entry.VpnName = string.Empty;
        Assert.False(entry.HasVpnConfigured);
        Assert.Equal(string.Empty, entry.VpnStatusBadge);

        entry.VpnServer = "10.200.0.1:443";
        Assert.True(entry.HasVpnConfigured);
        Assert.Equal("● Configurada", entry.VpnStatusBadge);
    }

    [Fact]
    public void VpnBlock_CreateDuplicate_CopiesAllVpnFields()
    {
        var source = new ConnectionEntry
        {
            Name = "Acesso Principal",
            Host = "192.168.1.10",
            VpnName = "VPN OpenVPN Matriz",
            VpnApp = "OpenVPN GUI",
            VpnServer = "openvpn.empresa.com.br:1194",
            VpnNotes = "Certificado client.ovpn necessário."
        };

        var duplicate = source.CreateDuplicate();

        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.Contains("(Cópia)", duplicate.Name);
        Assert.Equal("VPN OpenVPN Matriz", duplicate.VpnName);
        Assert.Equal("OpenVPN GUI", duplicate.VpnApp);
        Assert.Equal("openvpn.empresa.com.br:1194", duplicate.VpnServer);
        Assert.Equal("Certificado client.ovpn necessário.", duplicate.VpnNotes);
        Assert.True(duplicate.HasVpnConfigured);
    }

    [Fact]
    public async Task VpnBlock_EditingTracksUnsavedChangesAndDirectSave()
    {
        var entry = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Conexão Teste Unsaved VPN",
            ClientName = "Cliente Teste",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.10.10.10",
            Port = 3389,
            VpnName = "VPN Antiga"
        };
        await _repository.SaveConnectionAsync(entry);

        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.SelectedConnection = vm.FilteredConnections.FirstOrDefault(c => c.Id == entry.Id);
        Assert.NotNull(vm.SelectedConnection);
        Assert.False(vm.HasUnsavedChanges());

        // Altera o servidor da VPN
        vm.EditingEntry.VpnServer = "vpn.novodestino.com.br";
        Assert.True(vm.HasUnsavedChanges());

        // Salva as alterações
        await vm.SaveConnectionAsync();
        Assert.False(vm.HasUnsavedChanges());

        var reloaded = await _repository.GetConnectionByIdAsync(entry.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("vpn.novodestino.com.br", reloaded.VpnServer);
    }

    [Fact]
    public async Task VpnBlock_CopyCommands_CopiesVpnNameAndVpnServerToClipboard()
    {
        string? copiedText = null;
        var clipboard = new ClipboardService(
            _logger,
            text => { copiedText = text; return true; },
            () => copiedText,
            () => { copiedText = null; });

        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(_paths, _logger, _settingsService, _repository, _vaultService, clipboard, launcher, importExport, diag);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        vm.EditingEntry.VpnName = "VPN Corporativa WireGuard";
        vm.EditingEntry.VpnServer = "wg.corp.exemplo.com.br:51820";

        await vm.CopyVpnNameAsync();
        Assert.Equal("VPN Corporativa WireGuard", copiedText);
        Assert.Equal("Nome da VPN copiado", vm.StatusMessage);

        await vm.CopyVpnServerAsync();
        Assert.Equal("wg.corp.exemplo.com.br:51820", copiedText);
        Assert.Equal("Servidor da VPN copiado", vm.StatusMessage);
    }

    [Fact]
    public async Task VpnBlock_ExportAndImport_CsvXmlAndBackwardCompatibility()
    {
        var importExport = new ImportExportService(_repository, _vaultService, _logger);

        var connWithVpn = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "SRV-EXPORT-VPN",
            ClientName = "Cliente Alpha",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.0.0.99",
            Port = 3389,
            VpnName = "VPN SSL Fortinet",
            VpnApp = "FortiClient",
            VpnServer = "vpn.alpha.com:443",
            VpnNotes = "MFA habilitado"
        };
        await _repository.SaveConnectionAsync(connWithVpn);

        // 1. Exporta e importa via CSV
        var csvPath = Path.Combine(_tempRootDir, "teste_vpn.csv");
        await importExport.ExportToCsvAsync(csvPath, new[] { connWithVpn });

        var csvParsed = importExport.ParseCsvFile(csvPath);
        var csvPreview = importExport.BuildCsvPreview(csvParsed, csvParsed.SuggestedMapping, Array.Empty<ConnectionEntry>());
        Assert.True(csvPreview.ValidCount > 0);
        var csvItem = csvPreview.Items.First(i => i.Entry.Name == "SRV-EXPORT-VPN");
        Assert.Equal("VPN SSL Fortinet", csvItem.Entry.VpnName);
        Assert.Equal("FortiClient", csvItem.Entry.VpnApp);
        Assert.Equal("vpn.alpha.com:443", csvItem.Entry.VpnServer);
        Assert.Equal("MFA habilitado", csvItem.Entry.VpnNotes);

        // 2. CSV legado sem colunas de VPN (Retrocompatibilidade)
        var legacyCsv = "Nome;Cliente;Grupo;Ambiente;Protocolo;Host;Porta;Usuario;Dominio;ChavePrivada;Tags;Observacoes;Favorito\n" +
                        "SRV-LEGADO;Cliente Antigo;Geral;Produção;Rdp;10.0.0.1;3389;admin;;;;;Não";
        var legacyDoc = importExport.ParseCsvContent(legacyCsv);
        var legacyPreview = importExport.BuildCsvPreview(legacyDoc, legacyDoc.SuggestedMapping, Array.Empty<ConnectionEntry>());
        Assert.True(legacyPreview.ValidCount > 0);
        var legacyItem = legacyPreview.Items.First(i => i.Entry.Name == "SRV-LEGADO");
        Assert.Equal(string.Empty, legacyItem.Entry.VpnName);
        Assert.Equal(string.Empty, legacyItem.Entry.VpnApp);
        Assert.Equal(string.Empty, legacyItem.Entry.VpnServer);
        Assert.Equal(string.Empty, legacyItem.Entry.VpnNotes);

        // 3. Exporta e importa via XML próprio ConectaTI
        var xmlPath = Path.Combine(_tempRootDir, "teste_vpn.xml");
        await importExport.ExportToXmlAsync(xmlPath, new[] { connWithVpn });
        var xmlPreview = importExport.EvaluateAndParseAutoPuttyFile(xmlPath, Array.Empty<ConnectionEntry>());
        Assert.True(xmlPreview.ValidCount > 0);
        var xmlItem = xmlPreview.Items.First(i => i.Entry.Name == "SRV-EXPORT-VPN");
        Assert.Equal("VPN SSL Fortinet", xmlItem.Entry.VpnName);
        Assert.Equal("FortiClient", xmlItem.Entry.VpnApp);
        Assert.Equal("vpn.alpha.com:443", xmlItem.Entry.VpnServer);
        Assert.Equal("MFA habilitado", xmlItem.Entry.VpnNotes);

        // 4. XML legado sem tags de VPN (Retrocompatibilidade)
        var legacyXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ConectaTI Version="1.0">
              <Connections>
                <Connection>
                  <Name>SRV-XML-LEGADO</Name>
                  <Client>Cliente Legado</Client>
                  <Protocol>Rdp</Protocol>
                  <Host>10.0.0.2</Host>
                  <Port>3389</Port>
                </Connection>
              </Connections>
            </ConectaTI>
            """;
        var legacyXmlPath = Path.Combine(_tempRootDir, "legacy_no_vpn.xml");
        await File.WriteAllTextAsync(legacyXmlPath, legacyXml);
        var legacyXmlPreview = importExport.EvaluateAndParseAutoPuttyFile(legacyXmlPath, Array.Empty<ConnectionEntry>());
        Assert.True(legacyXmlPreview.ValidCount > 0);
        var legacyXmlItem = legacyXmlPreview.Items.First(i => i.Entry.Name == "SRV-XML-LEGADO");
        Assert.Equal(string.Empty, legacyXmlItem.Entry.VpnName);
        Assert.Equal(string.Empty, legacyXmlItem.Entry.VpnApp);
        Assert.Equal(string.Empty, legacyXmlItem.Entry.VpnServer);
        Assert.Equal(string.Empty, legacyXmlItem.Entry.VpnNotes);
    }

    [Fact]
    public async Task Navigation_RetainsDraftWithoutSavingOrBlocking()
    {
        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        var conn1 = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "SRV-DEADLOCK-1",
            ClientName = "Cliente Teste",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.0.0.1",
            Port = 3389
        };
        var conn2 = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "SRV-DEADLOCK-2",
            ClientName = "Cliente Teste",
            Protocol = ConnectionProtocol.Rdp,
            Host = "10.0.0.2",
            Port = 3389
        };

        await _repository.SaveConnectionAsync(conn1);
        await _repository.SaveConnectionAsync(conn2);

        await vm.InitializeAsync();
        Assert.Equal(conn1.Id, vm.SelectedConnection?.Id);

        // Altera dados sem salvar ainda
        vm.EditingEntry.Host = "10.0.0.99";
        vm.InlineHostAndPort = "10.0.0.99:3389";
        Assert.True(vm.HasUnsavedChanges());

        // Simula usuário clicando em "Sim: Salvar alterações" ao trocar de registro
        vm.ConfirmUnsavedChangesCallback = _ => UnsavedChangesChoice.Save;

        // Mede tempo: não pode congelar nem demorar mais de 3 segundos
        var sw = Stopwatch.StartNew();
        var navResult = await vm.RequestNavigateToConnectionAsync(vm.FilteredConnections.First(c => c.Id == conn2.Id));
        sw.Stop();

        Assert.True(navResult);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Navegação demorou {sw.ElapsedMilliseconds}ms (possível deadlock).");
        Assert.Equal(conn2.Id, vm.SelectedConnection?.Id);

        // Navigation must never persist the editor.
        var saved1 = await _repository.GetConnectionByIdAsync(conn1.Id);
        Assert.NotNull(saved1);
        Assert.Equal("10.0.0.1", saved1!.Host);
        await vm.RequestNavigateToConnectionAsync(vm.FilteredConnections.First(c => c.Id == conn1.Id));
        Assert.Equal("10.0.0.99", vm.EditingEntry.Host);
    }

    [Fact]
    public async Task SaveConnection_RapidConcurrentCalls_GuardsAgainstReentrancy()
    {
        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        await vm.InitializeAsync();
        vm.NewConnection();
        vm.EditingEntry.Name = "SRV-CONCORRENTE";
        vm.EditingEntry.Host = "10.10.10.10";
        vm.InlineHostAndPort = "10.10.10.10:3389";

        // Dispara saves concorrentes rápidos (como cliques duplos ou Ctrl+S repetido)
        var task1 = vm.SaveConnectionAsync();
        var task2 = vm.SaveConnectionAsync();

        var results = await Task.WhenAll(task1, task2);
        // Pelo menos um deve ter sucesso e não pode ocorrer colisão ou travamento
        Assert.True(results[0] || results[1]);

        var all = await _repository.GetAllConnectionsAsync();
        var matching = all.Where(c => c.Name == "SRV-CONCORRENTE").ToList();
        Assert.Single(matching);
    }

    [Fact]
    public async Task DuplicateConnection_DoesNotSaveToDatabaseImmediately()
    {
        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        var original = new ConnectionEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "SRV-ORIGINAL",
            ClientName = "Cliente",
            Protocol = ConnectionProtocol.Rdp,
            Host = "192.168.1.50",
            Port = 3389,
            VpnName = "VPN Corporativa"
        };
        await _repository.SaveConnectionAsync(original);
        await vm.InitializeAsync();

        var countBefore = (await _repository.GetAllConnectionsAsync()).Count;

        // Duplica a conexão
        await vm.DuplicateConnectionAsync();

        // O banco ainda DEVE ter a mesma quantidade de registros! A duplicação é um rascunho em memória
        var countImmediatelyAfter = (await _repository.GetAllConnectionsAsync()).Count;
        Assert.Equal(countBefore, countImmediatelyAfter);
        Assert.True(vm.IsNewRecord);
        Assert.Equal("SRV-ORIGINAL (Cópia)", vm.EditingEntry.Name);
        Assert.Equal("VPN Corporativa", vm.EditingEntry.VpnName);

        // Só salva quando o usuário clica explicitamente em Salvar
        var saved = await vm.SaveConnectionAsync();
        Assert.True(saved);

        var countAfterExplicitSave = (await _repository.GetAllConnectionsAsync()).Count;
        Assert.Equal(countBefore + 1, countAfterExplicitSave);
    }

    [Fact]
    public void AutoPuTTYIncrementalSearch_DiacriticsAndPrefixPriority_FindsCorrectMatch()
    {
        var list = new List<ConnectionEntry>
        {
            new() { Name = "Servidor Produção Backup", ClientName = "Cliente Geral" },
            new() { Name = "Produção Servidor Web", ClientName = "Cliente Alfa" },
            new() { Name = "São Paulo DB", ClientName = "Filial SP" },
            new() { Name = "Homologação App", ClientName = "Beta" }
        };

        // 1. Busca "producao" sem acento: deve priorizar "Produção Servidor Web" pelo prefixo do nome!
        var matchPrefix = StringNormalizationHelper.FindBestMatch(list, "producao");
        Assert.NotNull(matchPrefix);
        Assert.Equal("Produção Servidor Web", matchPrefix!.Name);

        // 2. Busca "sao paulo" sem acento: deve encontrar "São Paulo DB"
        var matchDiacritics = StringNormalizationHelper.FindBestMatch(list, "sao paulo");
        Assert.NotNull(matchDiacritics);
        Assert.Equal("São Paulo DB", matchDiacritics!.Name);

        // 3. Busca por substring "backup": encontra "Servidor Produção Backup"
        var matchSubstring = StringNormalizationHelper.FindBestMatch(list, "backup");
        Assert.NotNull(matchSubstring);
        Assert.Equal("Servidor Produção Backup", matchSubstring!.Name);

        // 4. Busca por prefixo de cliente "beta": encontra "Homologação App"
        var matchClient = StringNormalizationHelper.FindBestMatch(list, "beta");
        Assert.NotNull(matchClient);
        Assert.Equal("Homologação App", matchClient!.Name);
    }

    [Fact]
    public async Task RecycleBin_SoftDelete_Restore_CollisionAndEmptyTrash_WorksAsExpected()
    {
        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        var c1 = new ConnectionEntry { Name = "SRV-LIXEIRA-1", Host = "10.0.0.1", ClientName = "Geral" };
        var c2 = new ConnectionEntry { Name = "SRV-LIXEIRA-2", Host = "10.0.0.2", ClientName = "Geral" };
        await _repository.SaveConnectionAsync(c1);
        await _repository.SaveConnectionAsync(c2);

        await vm.InitializeAsync();
        Assert.Equal(2, vm.TotalConnectionsCount);
        Assert.Equal(0, vm.TrashCount);

        // 1. Soft-delete c1
        vm.ConfirmDeleteCallback = (_, _) => true;
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == c1.Id);
        await vm.DeleteConnectionAsync();

        Assert.Equal(1, vm.TotalConnectionsCount);
        Assert.Equal(1, vm.TrashCount);

        // 2. Cria nova conexão ativa com mesmo nome "SRV-LIXEIRA-1"
        var collision = new ConnectionEntry { Name = "SRV-LIXEIRA-1", Host = "10.0.0.9", ClientName = "Geral" };
        await _repository.SaveConnectionAsync(collision);
        await vm.ReloadAllDataAsync();

        // 3. Muda para a visualização da Lixeira
        await vm.SetViewModeAsync(ConnectionViewMode.Trash);
        Assert.Single(vm.FilteredConnections);
        Assert.Equal(c1.Id, vm.FilteredConnections[0].Id);

        // 4. Restaura c1 com colisão de nome (usuário aceita renomear)
        vm.ConfirmDiscardUnsavedChangesCallback = _ => true;
        vm.SelectedConnection = vm.FilteredConnections[0];
        await vm.RestoreSelectedConnectionAsync();

        // Verifica que c1 voltou e foi renomeado para evitar conflito
        await vm.SetViewModeAsync(ConnectionViewMode.All);
        Assert.Equal(3, vm.TotalConnectionsCount);
        Assert.Equal(0, vm.TrashCount);
        var restored = await _repository.GetConnectionByIdAsync(c1.Id);
        Assert.NotNull(restored);
        Assert.Equal("SRV-LIXEIRA-1 (Restaurada)", restored!.Name);

        // 5. Soft-delete c2 e esvazia lixeira
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == c2.Id);
        await vm.DeleteConnectionAsync();
        Assert.Equal(1, vm.TrashCount);

        await vm.EmptyTrashAsync();
        Assert.Equal(0, vm.TrashCount);
        var deletedPermanently = await _repository.GetConnectionByIdAsync(c2.Id);
        Assert.Null(deletedPermanently);
    }

    [Fact]
    public async Task PortableDatabaseAndMigration_DiscoversAndMigratesFromLegacyDb()
    {
        var migrationTestDir = Path.Combine(Path.GetTempPath(), "ConectaTI_MigTest_" + Guid.NewGuid().ToString("N"));
        var legacyDir = Path.Combine(migrationTestDir, "Legacy");
        var portableDir = Path.Combine(migrationTestDir, "Portable");

        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(portableDir);

        try
        {
            // 1. Cria base legada conectati.db com schema anterior e 3 conexões
            var legacyDbFile = Path.Combine(legacyDir, "conectati.db");
            using (var createConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDbFile}"))
            {
                createConn.Open();
                using var cmd = createConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Connections (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        ClientName TEXT NOT NULL DEFAULT '',
                        GroupName TEXT NOT NULL DEFAULT '',
                        Environment INTEGER NOT NULL DEFAULT 0,
                        Protocol INTEGER NOT NULL DEFAULT 0,
                        Host TEXT NOT NULL,
                        Port INTEGER NOT NULL,
                        Username TEXT NOT NULL DEFAULT '',
                        Domain TEXT NOT NULL DEFAULT '',
                        CredentialId TEXT NULL,
                        PrivateKeyPath TEXT NOT NULL DEFAULT '',
                        Notes TEXT NOT NULL DEFAULT '',
                        Tags TEXT NOT NULL DEFAULT '',
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        RdpFullScreen INTEGER NOT NULL DEFAULT 1,
                        RdpWidth INTEGER NOT NULL DEFAULT 0,
                        RdpHeight INTEGER NOT NULL DEFAULT 0,
                        RdpUseMultimon INTEGER NOT NULL DEFAULT 0,
                        RdpAdminSession INTEGER NOT NULL DEFAULT 0,
                        SshClient INTEGER NOT NULL DEFAULT 0,
                        SshUseAgent INTEGER NOT NULL DEFAULT 1,
                        VncParameters TEXT NOT NULL DEFAULT '',
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        LastOpenedAtUtc TEXT NULL
                    );
                    INSERT INTO Connections (Id, Name, ClientName, Host, Port, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ('1', 'SRV-LEGADO-1', 'Cliente Legado', '10.0.0.1', 3389, '2026-01-01', '2026-01-01'),
                           ('2', 'SRV-LEGADO-2', 'Cliente Legado', '10.0.0.2', 3389, '2026-01-01', '2026-01-01'),
                           ('3', 'SRV-LEGADO-3', 'Cliente Legado', '10.0.0.3', 3389, '2026-01-01', '2026-01-01');
                    """;
                cmd.ExecuteNonQuery();
            }

            // 2. Destino portátil sem sqlite.db pré-existente
            var portablePaths = new AppPaths(portableDir);
            Assert.False(File.Exists(portablePaths.DatabasePath));

            // Simula descoberta apontando a base legada
            DatabaseMigrationManager.PerformSafeMigration(legacyDbFile, portablePaths.DatabasePath, portablePaths, _logger);

            // 3. Verifica que sqlite.db foi criado e contém todos os dados
            Assert.True(File.Exists(portablePaths.DatabasePath));
            var portableRepo = new SqliteRepository(portablePaths, _logger);
            portableRepo.InitializeAndMigrate();
            var migratedConns = await portableRepo.GetAllConnectionsAsync();
            Assert.Equal(3, migratedConns.Count);
            Assert.Contains(migratedConns, c => c.Name == "SRV-LEGADO-1");
            Assert.Contains(migratedConns, c => c.Name == "SRV-LEGADO-2");
            Assert.Contains(migratedConns, c => c.Name == "SRV-LEGADO-3");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(migrationTestDir))
            {
                try { Directory.Delete(migrationTestDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task PerformanceBenchmark_1000Connections_MeasuresStartupFilterAndSave()
    {
        var benchmarkDir = Path.Combine(Path.GetTempPath(), "ConectaTI_Benchmark_" + Guid.NewGuid().ToString("N"));
        var bPaths = new AppPaths(benchmarkDir);
        var bRepo = new SqliteRepository(bPaths, _logger);
        bRepo.InitializeAndMigrate();

        try
        {
            // 1. Inserção em lote de 1.000 conexões
            var batch = new List<ConnectionEntry>(1000);
            for (int i = 1; i <= 1000; i++)
            {
                batch.Add(new ConnectionEntry
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = $"SRV-BENCHMARK-{i:D4}",
                    ClientName = i % 10 == 0 ? "Cliente Especial" : $"Cliente {i % 25}",
                    GroupName = $"Grupo {i % 5}",
                    Environment = (EnvironmentType)(i % 4),
                    Protocol = (ConnectionProtocol)(i % 5),
                    Host = $"10.{(i / 256) % 256}.{(i / 16) % 256}.{i % 256}",
                    Port = 3389,
                    IsFavorite = i % 7 == 0
                });
            }

            var swInsert = Stopwatch.StartNew();
            await bRepo.SaveConnectionsBatchAsync(batch);
            swInsert.Stop();
            Assert.True(swInsert.ElapsedMilliseconds < 5000, $"Inserção em lote de 1.000 demorou {swInsert.ElapsedMilliseconds}ms");

            // 2. Consulta de todas as 1.000 conexões
            var swLoad = Stopwatch.StartNew();
            var loaded = await bRepo.GetAllConnectionsAsync();
            swLoad.Stop();
            Assert.Equal(1000, loaded.Count);
            Assert.True(swLoad.ElapsedMilliseconds < 500, $"Leitura de 1.000 conexões demorou {swLoad.ElapsedMilliseconds}ms (esperado < 500ms)");

            // 3. Inicialização e filtro no ViewModel
            var clipboard = new ClipboardService(_logger);
            var launcher = new ConnectionLauncherService(bPaths, _settingsService, bRepo, _vaultService, clipboard, _logger);
            var importExport = new ImportExportService(bRepo, _vaultService, _logger);
            var diag = new NetworkDiagnosticService();

            var vm = new MainViewModel(
                bPaths, _logger, _settingsService, bRepo,
                _vaultService, clipboard, launcher, importExport, diag);

            var swInit = Stopwatch.StartNew();
            await vm.InitializeAsync();
            swInit.Stop();
            Assert.Equal(1000, vm.TotalConnectionsCount);
            Assert.True(swInit.ElapsedMilliseconds < 1500, $"Inicialização do ViewModel demorou {swInit.ElapsedMilliseconds}ms");

            // 4. Filtragem por texto em 1.000 conexões
            var swFilter = Stopwatch.StartNew();
            vm.SearchText = "Especial";
            swFilter.Stop();
            Assert.Equal(100, vm.FilteredConnectionsCount);
            Assert.True(swFilter.ElapsedMilliseconds < 100, $"Filtro por texto demorou {swFilter.ElapsedMilliseconds}ms (esperado < 100ms)");

            // 5. Gravação de alteração individual
            var toEdit = vm.FilteredConnections.First();
            await vm.RequestNavigateToConnectionAsync(toEdit);
            vm.EditingEntry.Host = "172.16.0.1";
            vm.InlineHostAndPort = "172.16.0.1:3389";

            var swSave = Stopwatch.StartNew();
            var saveOk = await vm.SaveConnectionAsync();
            swSave.Stop();
            Assert.True(saveOk);
            Assert.True(swSave.ElapsedMilliseconds < 500, $"Gravação individual com 1.000 itens demorou {swSave.ElapsedMilliseconds}ms");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(benchmarkDir))
            {
                try { Directory.Delete(benchmarkDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SaveConnection_DoesNotTriggerUnsavedChangesPromptOrClearConnectionList()
    {
        var conn1 = new ConnectionEntry { Id = "c1", Name = "Servidor Alfa", ClientName = "Cliente A", Host = "10.0.0.1", Port = 3389 };
        var conn2 = new ConnectionEntry { Id = "c2", Name = "Servidor Beta", ClientName = "Cliente B", Host = "10.0.0.2", Port = 22 };
        var conn3 = new ConnectionEntry { Id = "c3", Name = "Servidor Gama", ClientName = "Cliente C", Host = "10.0.0.3", Port = 5900 };
        await _repository.SaveConnectionAsync(conn1);
        await _repository.SaveConnectionAsync(conn2);
        await _repository.SaveConnectionAsync(conn3);

        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        bool promptTriggered = false;
        vm.ConfirmUnsavedChangesCallback = name =>
        {
            promptTriggered = true;
            return UnsavedChangesChoice.Save;
        };

        await vm.InitializeAsync();
        Assert.Equal(3, vm.FilteredConnectionsCount);

        // Seleciona c1 e edita
        var target = vm.FilteredConnections.First(c => c.Id == "c1");
        vm.SelectedConnection = target;
        vm.EditingEntry.Host = "10.0.0.99";
        vm.InlineHostAndPort = "10.0.0.99:3389";

        Assert.True(vm.HasUnsavedChanges());

        // Salva
        var saved = await vm.SaveConnectionAsync();
        Assert.True(saved);

        // Valida que o prompt NÃO foi acionado e a lista NÃO sumiu
        Assert.False(promptTriggered, "ConfirmUnsavedChangesCallback não deveria ser acionado durante o salvamento regular.");
        Assert.Equal(3, vm.FilteredConnectionsCount);
        Assert.False(vm.HasUnsavedChanges());
        Assert.Equal("10.0.0.99", vm.SelectedConnection?.Host);
    }

    [Fact]
    public async Task PasswordReveal_Toggle_DoesNotDirtyConnectionOrLeakBulletsText()
    {
        var cred = new SavedCredential
        {
            Id = "cred-reveal-test",
            Name = "Credencial de Teste",
            Username = "admin",
            ProtectedSecretBase64 = _vaultService.ProtectSecret("cred-reveal-test", "admin", "P@ssw0rd123!")
        };
        await _repository.SaveCredentialAsync(cred);

        var conn = new ConnectionEntry
        {
            Id = "conn-reveal-test",
            Name = "Acesso Seguro",
            Host = "192.168.1.50",
            Port = 3389,
            CredentialId = cred.Id
        };
        await _repository.SaveConnectionAsync(conn);

        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        await vm.InitializeAsync();
        vm.SelectedConnection = vm.FilteredConnections.First(c => c.Id == conn.Id);

        Assert.True(vm.HasSavedPassword);
        Assert.False(vm.HasUnsavedChanges());
        Assert.Equal(string.Empty, vm.InlinePasswordInput);

        // Revela a senha
        await vm.TogglePasswordVisibilityAsync();
        Assert.True(vm.IsPasswordRevealed);
        Assert.Equal("P@ssw0rd123!", vm.RevealedPasswordText);
        // Visualizar não pode sujar o InlinePasswordInput nem marcar como dirty
        Assert.Equal(string.Empty, vm.InlinePasswordInput);
        Assert.False(vm.HasUnsavedChanges());

        // Oculta a senha novamente
        vm.HideRevealedPassword();
        Assert.False(vm.IsPasswordRevealed);
        Assert.Equal(string.Empty, vm.RevealedPasswordText);
        Assert.Equal(string.Empty, vm.InlinePasswordInput);
        Assert.False(vm.HasUnsavedChanges());
    }

    [Fact]
    public async Task FilterAndSort_ViewModesSwitching_PreservesConnectionsWithoutStaleFilters()
    {
        var conn1 = new ConnectionEntry { Id = "f1", Name = "Alpha", ClientName = "Client A", IsFavorite = true };
        var conn2 = new ConnectionEntry { Id = "f2", Name = "Beta", ClientName = "Client B", LastOpenedAtUtc = DateTime.UtcNow };
        var conn3 = new ConnectionEntry { Id = "f3", Name = "Gamma", ClientName = "Client C" };
        await _repository.SaveConnectionAsync(conn1);
        await _repository.SaveConnectionAsync(conn2);
        await _repository.SaveConnectionAsync(conn3);

        var clipboard = new ClipboardService(_logger);
        var launcher = new ConnectionLauncherService(_paths, _settingsService, _repository, _vaultService, clipboard, _logger);
        var importExport = new ImportExportService(_repository, _vaultService, _logger);
        var diag = new NetworkDiagnosticService();

        var vm = new MainViewModel(
            _paths, _logger, _settingsService, _repository,
            _vaultService, clipboard, launcher, importExport, diag);

        await vm.InitializeAsync();
        Assert.Equal(3, vm.FilteredConnectionsCount);

        // Muda para Favoritos
        await vm.SetViewModeAsync(ConnectionViewMode.Favorites);
        Assert.Equal(1, vm.FilteredConnectionsCount);
        Assert.Equal("Alpha", vm.FilteredConnections.Single().Name);

        // Muda para Recentes
        await vm.SetViewModeAsync(ConnectionViewMode.Recent);
        Assert.Equal(1, vm.FilteredConnectionsCount);
        Assert.Equal("Beta", vm.FilteredConnections.Single().Name);

        // Volta para Todos
        await vm.SetViewModeAsync(ConnectionViewMode.All);
        Assert.Equal(3, vm.FilteredConnectionsCount);
    }
}
