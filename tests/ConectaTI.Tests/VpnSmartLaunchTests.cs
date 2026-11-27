using System.Diagnostics;
using System.IO;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class VpnSmartLaunchTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Vpn_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void VpnLauncher_PrioritizesFortiClientConsoleOverTray_ResolvesRadminVpn_AndSupportsFortiSslCli()
    {
        Assert.Contains("Radmin VPN", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("OpenVPN GUI", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("FortiClient", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("Tailscale", VpnClientLauncherService.CommonVpnClientPresets);

        var fakeProgFiles = Path.Combine(_paths.RootDirectory, "ProgramFiles");
        var fakeProgFilesX86 = Path.Combine(_paths.RootDirectory, "ProgramFilesX86");
        var fakeStartMenu = Path.Combine(_paths.RootDirectory, "StartMenu");

        var fortiDir = Path.Combine(fakeProgFiles, "Fortinet", "FortiClient");
        var fortiTrayExe = Path.Combine(fortiDir, "FortiTray.exe");
        var fortiExe = Path.Combine(fortiDir, "FortiClient.exe");
        var fortiConsoleExe = Path.Combine(fortiDir, "FortiClientConsole.exe");
        var fortiSslCliExe = Path.Combine(fortiDir, "FortiSSLVPNclient.exe");
        var openConnectExe = Path.Combine(fakeProgFiles, "OpenConnect-GUI", "openconnect-gui.exe");
        var radminGuiExe = Path.Combine(fakeProgFilesX86, "Radmin VPN", "RvRvpnGui.exe");
        Directory.CreateDirectory(fortiDir);
        Directory.CreateDirectory(Path.GetDirectoryName(openConnectExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(radminGuiExe)!);
        File.WriteAllText(fortiTrayExe, "MZ-fictitious");
        File.WriteAllText(fortiExe, "MZ-fictitious");
        File.WriteAllText(fortiConsoleExe, "MZ-fictitious");
        File.WriteAllText(fortiSslCliExe, "MZ-fictitious");
        File.WriteAllText(openConnectExe, "MZ-fictitious");
        File.WriteAllText(radminGuiExe, "MZ-fictitious");

        var sonicDir = Path.Combine(fakeStartMenu, "SonicWall");
        Directory.CreateDirectory(sonicDir);
        var uninstallLnk = Path.Combine(sonicDir, "Uninstall NetExtender.lnk");
        var realLnk = Path.Combine(sonicDir, "SonicWall NetExtender.lnk");
        File.WriteAllText(uninstallLnk, "shortcut");
        File.WriteAllText(realLnk, "shortcut");

        var launchedInfos = new List<ProcessStartInfo>();
        var service = new VpnClientLauncherService(
            customStarter: psi => { launchedInfos.Add(psi); return true; },
            customProgramRoots: [fakeProgFiles, fakeProgFilesX86],
            customStartMenuRoots: [fakeStartMenu],
            customIsProcessRunning: procName => procName.Equals("FortiTray", StringComparison.OrdinalIgnoreCase));

        // 1. Mesmo com FortiTray em execução na bandeja, "fortcliente" aciona FortiClientConsole.exe (nunca FortiTray.exe) com --force-renderer-accessibility
        var fortiRes = service.Resolve(new ConnectionEntry
        {
            VpnName = "VPN Matriz",
            VpnApp = "fortcliente",
            VpnServer = "vpn.cliente1.example.test:10443"
        });
        Assert.True(fortiRes.Found);
        Assert.Equal("FortiClient", fortiRes.DisplayClientName);
        Assert.Equal(fortiConsoleExe, fortiRes.TargetPathOrUri);
        Assert.Equal(["--force-renderer-accessibility"], fortiRes.Arguments);
        Assert.Equal("VPN Matriz", fortiRes.MatchedProfileName);
        Assert.Equal(VpnAutomationVendorKind.FortiClient, fortiRes.AutomationVendor);

        // 2. Se o caminho explícito apontar para FortiClient.exe, promove automaticamente para o irmão FortiClientConsole.exe
        var explicitFortiRes = service.Resolve(new ConnectionEntry { VpnName = "Tunel Filial", VpnApp = fortiExe });
        Assert.Equal(fortiConsoleExe, explicitFortiRes.TargetPathOrUri);
        Assert.Equal(["--force-renderer-accessibility"], explicitFortiRes.Arguments);

        // 3. FortiSSLVPNclient.exe explícito com credencial manual usa CLI oficial sem expor segredo em ToString
        var sslLogin = new VpnLoginCredential("analista", "", "SYNTHETIC_FORTI_SECRET_1");
        var fortiCliRes = service.Resolve(
            new ConnectionEntry { VpnName = "TunelCli", VpnApp = fortiSslCliExe },
            sslLogin);
        Assert.True(fortiCliRes.Found);
        Assert.True(fortiCliRes.PassedCredentialsDirectly);
        Assert.Equal(["connect", "-s", "TunelCli", "-u", "analista:SYNTHETIC_FORTI_SECRET_1"], fortiCliRes.Arguments);
        Assert.DoesNotContain("SYNTHETIC_FORTI_SECRET_1", fortiCliRes.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_FORTI_SECRET_1", sslLogin.ToString(), StringComparison.Ordinal);

        // 4. "radmin" -> RvRvpnGui.exe em Program Files (x86)\Radmin VPN
        var radminRes = service.Resolve(new ConnectionEntry { VpnApp = "radmin" });
        Assert.True(radminRes.Found);
        Assert.Equal("Radmin VPN", radminRes.DisplayClientName);
        Assert.Equal(radminGuiExe, radminRes.TargetPathOrUri);

        // 5. "open connect" -> openconnect-gui.exe com automação OpenConnectGui
        var ocRes = service.Resolve(new ConnectionEntry { VpnName = "Perfil OC", VpnApp = "open connect" });
        Assert.True(ocRes.Found);
        Assert.Equal("OpenConnect GUI", ocRes.DisplayClientName);
        Assert.Equal(openConnectExe, ocRes.TargetPathOrUri);
        Assert.Equal(VpnAutomationVendorKind.OpenConnectGui, ocRes.AutomationVendor);

        // 6. Atalho no Menu Iniciar ignorando "Uninstall"
        var lnkRes = service.Resolve(new ConnectionEntry { VpnApp = "net extender" });
        Assert.True(lnkRes.Found);
        Assert.Equal(realLnk, lnkRes.TargetPathOrUri);
        Assert.True(lnkRes.UseShellExecute);

        // 7. VpnApp vazio e sem vendor conhecido -> fallback para VPN nativa do Windows
        var winFallbackRes = service.Resolve(new ConnectionEntry { VpnName = "VPN L2TP Ficticia" });
        Assert.True(winFallbackRes.Found);
        Assert.True(winFallbackRes.IsWindowsNativeFallback);

        var outcome = service.Launch(new ConnectionEntry { VpnApp = "radmin" });
        Assert.True(outcome.Started);
        Assert.Single(launchedInfos);
        Assert.Equal(radminGuiExe, launchedInfos[0].FileName);
    }

    [Fact]
    public void CiscoVpnCli_BuildsInMemoryStandardInputPipe_WithoutExposingPasswordInArgumentsOrToString()
    {
        var fakeProgFiles = Path.Combine(_paths.RootDirectory, "ProgramFiles");
        var ciscoRoot = Path.Combine(fakeProgFiles, "Cisco", "Cisco Secure Client");
        var ciscoUiExe = Path.Combine(ciscoRoot, "UI", "csc_ui.exe");
        var ciscoCliExe = Path.Combine(ciscoRoot, "vpncli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ciscoUiExe)!);
        File.WriteAllText(ciscoUiExe, "MZ-fictitious");
        File.WriteAllText(ciscoCliExe, "MZ-fictitious");

        var service = new VpnClientLauncherService(
            customStarter: _ => true,
            customProgramRoots: [fakeProgFiles],
            customStartMenuRoots: []);

        var entry = new ConnectionEntry
        {
            VpnName = "Cisco Corporativo",
            VpnApp = "Cisco Secure Client (AnyConnect)",
            VpnServer = "vpn.ciscotest.example.test"
        };

        // Sem credencial manual: abre apenas a UI sem acionar vpncli -s
        var uiOnly = service.Resolve(entry, credential: null);
        Assert.Equal(ciscoUiExe, uiOnly.TargetPathOrUri);
        Assert.False(uiOnly.PassedCredentialsDirectly);
        Assert.Null(uiOnly.StandardInputPayload);

        // Com credencial manual: usa vpncli.exe -s via pipe StandardInput em memória, sem senha nos argumentos de linha de comando
        var manualCred = new VpnLoginCredential("suporte.ti", "CORP", "SYNTHETIC_CISCO_PASS_77");
        var cliRes = service.Resolve(entry, manualCred);
        Assert.Equal(ciscoCliExe, cliRes.TargetPathOrUri);
        Assert.Equal(["-s"], cliRes.Arguments);
        Assert.True(cliRes.PassedCredentialsDirectly);
        Assert.Equal("connect vpn.ciscotest.example.test\nCORP\\suporte.ti\nSYNTHETIC_CISCO_PASS_77\ny\n", cliRes.StandardInputPayload);
        Assert.DoesNotContain("SYNTHETIC_CISCO_PASS_77", cliRes.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenVpnGui_DiscoversOvpnProfiles_SwitchesTrayArgs_AndRespectsNonInterferenceWhenCredentialsEmpty()
    {
        var fakeProgFiles = Path.Combine(_paths.RootDirectory, "ProgramFiles");
        var openVpnExe = Path.Combine(fakeProgFiles, "OpenVPN", "bin", "openvpn-gui.exe");
        var openVpnConfigDir = Path.Combine(fakeProgFiles, "OpenVPN", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(openVpnExe)!);
        Directory.CreateDirectory(openVpnConfigDir);
        File.WriteAllText(openVpnExe, "MZ-fictitious");

        var alfaOvpn = Path.Combine(openVpnConfigDir, "AlfaVidros.ovpn");
        var betaOvpn = Path.Combine(openVpnConfigDir, "BetaMaq.ovpn");
        File.WriteAllText(alfaOvpn, "client\ndev tun\nproto tcp\nremote vpn.alfavidros.example.test 1194\nauth-user-pass\n");
        File.WriteAllText(betaOvpn, "client\ndev tun\nremote vpn.betamaq.example.test 1194\n");

        var trayRunning = false;
        var launched = new List<ProcessStartInfo>();
        var service = new VpnClientLauncherService(
            customStarter: psi => { launched.Add(psi); return true; },
            customProgramRoots: [fakeProgFiles],
            customStartMenuRoots: [],
            customOpenVpnConfigRoots: [openVpnConfigDir],
            customIsProcessRunning: name => name == "openvpn-gui" && trayRunning);

        var discovered = service.DiscoverInstalledVpnProfiles();
        Assert.Equal(2, discovered.Count);
        var alfaDiscovered = Assert.Single(discovered, p => p.ProfileName == "AlfaVidros");
        Assert.Equal("OpenVPN GUI", alfaDiscovered.SuggestedApp);
        Assert.Equal("vpn.alfavidros.example.test:1194", alfaDiscovered.SuggestedServer);

        var entry = new ConnectionEntry
        {
            Name = "AlfaVidros OpenVPN",
            VpnName = "AlfaVidros OpenGui",
            VpnApp = "OpenVPN GUI"
        };

        // Quando Usuário/Senha da VPN estão vazios (ex.: perfil .ovpn já tem credencial salva), AutomationVendor é None (não interfere)
        var nonInterferingRes = service.Resolve(entry, credential: null);
        Assert.Equal(VpnAutomationVendorKind.None, nonInterferingRes.AutomationVendor);

        // Quando Usuário/Senha da VPN são preenchidos manualmente, ativa preenchimento do diálogo Win32 de autenticação caso apareça
        var withManualCredRes = service.Resolve(entry, new VpnLoginCredential("operador", "", "SYNTHETIC_OVPN_PASS"));
        Assert.Equal(VpnAutomationVendorKind.OpenVpnGuiAuthDialog, withManualCredRes.AutomationVendor);

        // Caso A: OpenVPN GUI fechado -> inicia com --connect AlfaVidros.ovpn
        trayRunning = false;
        var closedOutcome = service.Launch(entry);
        Assert.True(closedOutcome.Started);
        Assert.Equal("AlfaVidros", closedOutcome.MatchedProfileName);
        Assert.Equal(["--connect", "AlfaVidros.ovpn"], launched[^1].ArgumentList);

        // Caso B: OpenVPN GUI já aberto na bandeja -> envia --command connect AlfaVidros (sem extensão .ovpn)
        trayRunning = true;
        var runningOutcome = service.Launch(entry);
        Assert.True(runningOutcome.Started);
        Assert.Equal(["--command", "connect", "AlfaVidros"], launched[^1].ArgumentList);
    }

    [Fact]
    public async Task ManualVpnUsernameAndPassword_SupportMigration12_NonInterferenceWhenEmpty_DraftIsolation_AndExplicitSaveOrRemove()
    {
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        settings.Load();
        settings.Current.RegisterRdpCredentialsInWindowsVault = false;
        settings.Save();

        var repo = new SqliteRepository(_paths, logger);
        repo.InitializeAndMigrate();
        Assert.Equal(12, repo.GetSchemaVersion());

        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        VaultTestSupport.Initialize(vault);

        string? lastClipboardText = null;
        var clipboard = new ClipboardService(logger, txt => { lastClipboardText = txt; return true; }, () => lastClipboardText, () => lastClipboardText = null);

        var fakeProgRoot = Path.Combine(_paths.RootDirectory, "ProgRoot");
        var fakeRasdial = Path.Combine(fakeProgRoot, "rasdial.exe");
        var fakeOpenVpn = Path.Combine(fakeProgRoot, "OpenVPN", "bin", "openvpn-gui.exe");
        var fakeOvpnDir = Path.Combine(fakeProgRoot, "OpenVPN", "config");
        var fakePbk = Path.Combine(_paths.RootDirectory, "rasphone.pbk");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeOpenVpn)!);
        Directory.CreateDirectory(fakeOvpnDir);
        File.WriteAllText(fakeRasdial, "MZ");
        File.WriteAllText(fakeOpenVpn, "MZ");
        File.WriteAllText(Path.Combine(fakeOvpnDir, "ClienteSalvo.ovpn"), "client\nremote vpn.clientesalvo.example.test 1194\n");
        File.WriteAllText(fakePbk, "[VPN Gama L2TP]\nPhoneNumber=vpn.gama.example.test\n");

        var startedProcesses = new List<ProcessStartInfo>();
        var vpnLauncher = new VpnClientLauncherService(
            logger,
            customStarter: psi => { startedProcesses.Add(psi); return true; },
            customProgramRoots: [fakeProgRoot],
            customStartMenuRoots: [],
            customOpenVpnConfigRoots: [fakeOvpnDir],
            customRasPhonebookFiles: [fakePbk],
            customIsProcessRunning: name => name == "openvpn-gui");

        var launcher = new ConnectionLauncherService(_paths, settings, repo, vault, clipboard, logger);
        var vm = new MainViewModel(
            _paths, logger, settings, repo, vault, clipboard,
            launcher, new ImportExportService(repo, vault, logger), new ConfigurableProbe(() => true), vpnLauncher);

        var entry = new ConnectionEntry
        {
            Id = "gama-srv-01",
            Name = "SRV-GAMA-ERP",
            ClientName = "Gama Fictícia",
            Host = "10.99.0.10",
            Port = 3389,
            VpnName = "ClienteSalvo",
            VpnApp = "OpenVPN GUI"
        };
        await repo.SaveConnectionAsync(entry);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // 1. Não interferência: Usuário da VPN e Senha da VPN vazios -> aciona .ovpn na bandeja e NUNCA toca na área de transferência
        await vm.OpenVpnClientAsync();
        Assert.Single(startedProcesses);
        Assert.Equal(fakeOpenVpn, startedProcesses[0].FileName);
        Assert.Equal(["--command", "connect", "ClienteSalvo"], startedProcesses[0].ArgumentList);
        Assert.Null(lastClipboardText);

        // 2. Rascunho manual em memória (sem salvar): preenche Usuário da VPN + Senha da VPN para VPN do Windows -> autentica via rasdial sem gravar no SQLite
        vm.EditingEntry.VpnName = "VPN Gama L2TP";
        vm.EditingEntry.VpnApp = "VPN do Windows";
        vm.EditingEntry.VpnUsername = @"GAMACORP\analista.vpn";
        vm.UpdateVpnPasswordFromUser("SYNTHETIC_VPN_PASS_99");

        await vm.OpenVpnClientAsync();
        Assert.Equal(2, startedProcesses.Count);
        Assert.Equal(fakeRasdial, startedProcesses[1].FileName);
        Assert.Equal(["VPN Gama L2TP", @"GAMACORP\analista.vpn", "SYNTHETIC_VPN_PASS_99"], startedProcesses[1].ArgumentList);
        Assert.Null(lastClipboardText);

        var persistedBeforeSave = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal(string.Empty, persistedBeforeSave.VpnUsername);
        Assert.Null(persistedBeforeSave.VpnCredentialId);

        // 3. Salvar explícito (Salvar / Ctrl+S): persiste VpnUsername e criptografa Senha da VPN no cofre SQLite
        Assert.True(await vm.SaveConnectionAsync());
        Assert.True(vm.HasSavedVpnPassword);
        Assert.Equal(string.Empty, vm.InlineVpnPasswordInput);

        var persistedAfterSave = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal(@"GAMACORP\analista.vpn", persistedAfterSave.VpnUsername);
        Assert.False(string.IsNullOrWhiteSpace(persistedAfterSave.VpnCredentialId));
        var savedVpnCredentialId = persistedAfterSave.VpnCredentialId!;

        // 4. Editar outro campo com caixa de senha da VPN vazia por proteção preserva a senha salva da VPN
        vm.EditingEntry.VpnNotes = "Observação fictícia";
        Assert.True(await vm.SaveConnectionAsync());
        var persistedAfterNotesEdit = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal(savedVpnCredentialId, persistedAfterNotesEdit.VpnCredentialId);

        // 5. Remoção explícita da senha da VPN só é efetivada ao clicar em Salvar
        vm.MarkVpnPasswordForRemoval();
        Assert.True(vm.IsVpnPasswordMarkedForRemoval);
        Assert.True(await vm.SaveConnectionAsync());

        var afterExplicitRemove = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Null(afterExplicitRemove.VpnCredentialId);
        Assert.Null(await repo.GetCredentialByIdAsync(savedVpnCredentialId));
    }

    [Fact]
    public async Task ConnectAsync_WhenServerUnreachableAndVpnConfigured_PromptsAndHandlesOpenVpnConnectAnywayOrCancel()
    {
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        settings.Load();

        var fakeMstsc = Path.Combine(_paths.RootDirectory, "fake-mstsc.exe");
        var fakeForti = Path.Combine(_paths.RootDirectory, "ProgramFiles", "Fortinet", "FortiClient", "FortiClientConsole.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeForti)!);
        File.WriteAllText(fakeMstsc, "MZ");
        File.WriteAllText(fakeForti, "MZ");
        settings.Current.MstscExecutablePath = fakeMstsc;
        settings.Current.RegisterRdpCredentialsInWindowsVault = false;
        settings.Save();

        var repo = new SqliteRepository(_paths, logger);
        repo.InitializeAndMigrate();
        var entry = new ConnectionEntry
        {
            Id = "senior-prod-1",
            Name = "SRV-SENIOR-ERP",
            ClientName = "Cliente Homolog",
            Host = "10.250.10.20",
            Port = 3389,
            VpnName = "VPN Cliente Homolog",
            VpnApp = "fortcliente",
            VpnServer = "vpn.homolog.example.test:10443"
        };
        await repo.SaveConnectionAsync(entry);

        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        VaultTestSupport.Initialize(vault);
        var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });

        var rdpStarts = 0;
        var vpnStarts = 0;
        var launcher = new ConnectionLauncherService(
            _paths, settings, repo, vault, clipboard, logger,
            customProcessStarter: _ => { rdpStarts++; return 1234; });
        var vpnLauncher = new VpnClientLauncherService(
            logger,
            customStarter: _ => { vpnStarts++; return true; },
            customProgramRoots: [Path.Combine(_paths.RootDirectory, "ProgramFiles")],
            customStartMenuRoots: []);

        var probeReachable = false;
        var probe = new ConfigurableProbe(() => probeReachable);
        var vm = new MainViewModel(
            _paths, logger, settings, repo, vault, clipboard,
            launcher, new ImportExportService(repo, vault, logger), probe, vpnLauncher);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // Mantém rascunho não salvo em memória para garantir que conectar/abrir VPN não persiste edições
        vm.EditingEntry.Notes = "Rascunho em memória não salvo";

        VpnUnreachableDecision nextDecision = VpnUnreachableDecision.OpenVpn;
        var promptCalls = 0;
        vm.PromptUnreachableVpnCallback = (_, _, resolvedClient) =>
        {
            promptCalls++;
            Assert.Equal("FortiClient", resolvedClient);
            return nextDecision;
        };

        // Caso 1: Servidor inacessível -> Analista escolhe "Conectar VPN" -> Abre FortiClient e NÃO abre MSTSC
        await vm.ConnectAsync();
        Assert.Equal(1, promptCalls);
        Assert.Equal(1, vpnStarts);
        Assert.Equal(0, rdpStarts);

        // Caso 2: Servidor inacessível -> Analista escolhe "Cancelar" -> Não abre VPN nem MSTSC
        nextDecision = VpnUnreachableDecision.Cancel;
        await vm.ConnectAsync();
        Assert.Equal(2, promptCalls);
        Assert.Equal(1, vpnStarts);
        Assert.Equal(0, rdpStarts);

        // Caso 3: Servidor inacessível -> Analista escolhe "Conectar mesmo assim" -> Abre MSTSC
        nextDecision = VpnUnreachableDecision.ConnectAnyway;
        await vm.ConnectAsync();
        Assert.Equal(3, promptCalls);
        Assert.Equal(1, vpnStarts);
        Assert.Equal(1, rdpStarts);

        // Caso 4: VPN já conectada (porta TCP acessível) -> Abre MSTSC direto sem exibir prompt
        probeReachable = true;
        await vm.ConnectAsync();
        Assert.Equal(3, promptCalls);
        Assert.Equal(2, rdpStarts);

        // Rascunho não foi gravado no banco por conectar
        var persisted = await repo.GetConnectionByIdAsync(entry.Id);
        Assert.Equal(string.Empty, persisted!.Notes);
    }

    private sealed class ConfigurableProbe(Func<bool> isReachable) : INetworkConnectivityProbe
    {
        public Task<NetworkDiagnosticResult> TestConnectivityAsync(
            string hostInput,
            int port,
            int timeoutMs = 4000,
            CancellationToken cancellationToken = default)
        {
            var ok = isReachable();
            return Task.FromResult(new NetworkDiagnosticResult(
                DnsResolved: true,
                TcpPortReachable: ok,
                ResolvedAddresses: ["10.250.10.20"],
                DnsDurationMs: 2,
                TcpDurationMs: ok ? 15 : 1200,
                SummaryTitle: ok ? "Porta acessível" : $"Timeout TCP na porta {port}",
                DetailedMessage: string.Empty));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch { }
    }
}
