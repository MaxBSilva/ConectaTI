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
    public void VpnLauncher_ResolvesMessySpellings_RadminVpn_StartMenuShortcuts_AndWindowsFallback()
    {
        Assert.Contains("Radmin VPN", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("OpenVPN GUI", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("FortiClient", VpnClientLauncherService.CommonVpnClientPresets);
        Assert.Contains("Tailscale", VpnClientLauncherService.CommonVpnClientPresets);

        var fakeProgFiles = Path.Combine(_paths.RootDirectory, "ProgramFiles");
        var fakeProgFilesX86 = Path.Combine(_paths.RootDirectory, "ProgramFilesX86");
        var fakeStartMenu = Path.Combine(_paths.RootDirectory, "StartMenu");

        var fortiExe = Path.Combine(fakeProgFiles, "Fortinet", "FortiClient", "FortiClient.exe");
        var openConnectExe = Path.Combine(fakeProgFiles, "OpenConnect-GUI", "openconnect-gui.exe");
        var radminGuiExe = Path.Combine(fakeProgFilesX86, "Radmin VPN", "RvRvpnGui.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fortiExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(openConnectExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(radminGuiExe)!);
        File.WriteAllText(fortiExe, "MZ-fictitious");
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
            customStartMenuRoots: [fakeStartMenu]);

        // 1. "fortcliente" (grafia comum com variação) -> FortiClient.exe
        var fortiRes = service.Resolve(new ConnectionEntry { VpnApp = "fortcliente", VpnServer = "vpn.cliente1.example.test:10443" });
        Assert.True(fortiRes.Found);
        Assert.Equal("FortiClient", fortiRes.DisplayClientName);
        Assert.Equal(fortiExe, fortiRes.TargetPathOrUri);

        // 2. "radmin" / "Radmin VPN" -> RvRvpnGui.exe em Program Files (x86)\Radmin VPN
        var radminRes = service.Resolve(new ConnectionEntry { VpnApp = "radmin" });
        Assert.True(radminRes.Found);
        Assert.Equal("Radmin VPN", radminRes.DisplayClientName);
        Assert.Equal(radminGuiExe, radminRes.TargetPathOrUri);

        // 3. "open connect" -> openconnect-gui.exe
        var ocRes = service.Resolve(new ConnectionEntry { VpnApp = "open connect" });
        Assert.True(ocRes.Found);
        Assert.Equal("OpenConnect GUI", ocRes.DisplayClientName);
        Assert.Equal(openConnectExe, ocRes.TargetPathOrUri);

        // 4. "open gui" -> quando OpenVPN não está instalado mas OpenConnect GUI está, resolve OpenConnect GUI
        var ogRes = service.Resolve(new ConnectionEntry { VpnApp = "open gui" });
        Assert.True(ogRes.Found);
        Assert.Equal(openConnectExe, ogRes.TargetPathOrUri);

        // 5. VpnApp vazio, mas o analista escreveu "Usar FortiClient" em VpnName/VpnNotes
        var notesFallbackRes = service.Resolve(new ConnectionEntry { VpnName = "VPN Matriz", VpnNotes = "Conectar pelo fortclient antes do ERP" });
        Assert.True(notesFallbackRes.Found);
        Assert.Equal(fortiExe, notesFallbackRes.TargetPathOrUri);

        // 6. Atalho no Menu Iniciar ignorando "Uninstall"
        var lnkRes = service.Resolve(new ConnectionEntry { VpnApp = "net extender" });
        Assert.True(lnkRes.Found);
        Assert.Equal(realLnk, lnkRes.TargetPathOrUri);
        Assert.True(lnkRes.UseShellExecute);

        // 7. VpnApp totalmente vazio e sem vendor conhecido -> fallback para VPN nativa do Windows
        var winFallbackRes = service.Resolve(new ConnectionEntry { VpnName = "VPN L2TP Ficticia" });
        Assert.True(winFallbackRes.Found);
        Assert.True(winFallbackRes.IsWindowsNativeFallback);

        var outcome = service.Launch(new ConnectionEntry { VpnApp = "radmin" });
        Assert.True(outcome.Started);
        Assert.Single(launchedInfos);
        Assert.Equal(radminGuiExe, launchedInfos[0].FileName);
    }

    [Fact]
    public void OpenVpnGui_DiscoversOvpnProfiles_MatchesStem_AndSwitchesArgsBetweenClosedAndRunningTray()
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

        // Cadastro com variação real de nome ("AlfaVidros OpenGui") encontra AlfaVidros.ovpn sem exigir renomeação
        var entry = new ConnectionEntry
        {
            Name = "AlfaVidros OpenVPN",
            VpnName = "AlfaVidros OpenGui",
            VpnApp = "OpenVPN GUI"
        };

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
    public async Task VpnCredentials_SupportMigration11_WindowsRasdialDirectLogin_AndGuiClipboardAssistWithoutAutoSavingDraft()
    {
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        settings.Load();
        settings.Current.RegisterRdpCredentialsInWindowsVault = false;
        settings.Save();

        var repo = new SqliteRepository(_paths, logger);
        repo.InitializeAndMigrate();
        Assert.Equal(11, repo.GetSchemaVersion());

        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        VaultTestSupport.Initialize(vault);

        string? lastClipboardText = null;
        var clipboard = new ClipboardService(logger, txt => { lastClipboardText = txt; return true; }, () => lastClipboardText, () => lastClipboardText = null);

        var vpnCred = new SavedCredential
        {
            Name = "VPN Cliente Gama",
            Username = "analista.vpn",
            Domain = "GAMACORP"
        };
        vpnCred.ProtectedSecretBase64 = vault.ProtectSecret(vpnCred.Id, vpnCred.EffectiveUserDisplay, "SYNTHETIC_VPN_PASS_99", writeToWindowsVault: false);
        await repo.SaveCredentialAsync(vpnCred);

        var fakeProgRoot = Path.Combine(_paths.RootDirectory, "ProgRoot");
        var fakeRasdial = Path.Combine(fakeProgRoot, "rasdial.exe");
        var fakeForti = Path.Combine(fakeProgRoot, "Fortinet", "FortiClient", "FortiClient.exe");
        var fakePbk = Path.Combine(_paths.RootDirectory, "rasphone.pbk");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeForti)!);
        File.WriteAllText(fakeRasdial, "MZ");
        File.WriteAllText(fakeForti, "MZ");
        File.WriteAllText(fakePbk, "[VPN Gama L2TP]\nPhoneNumber=vpn.gama.example.test\n");

        var startedProcesses = new List<ProcessStartInfo>();
        var vpnLauncher = new VpnClientLauncherService(
            logger,
            customStarter: psi => { startedProcesses.Add(psi); return true; },
            customProgramRoots: [fakeProgRoot],
            customStartMenuRoots: [],
            customRasPhonebookFiles: [fakePbk]);

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
            VpnName = "VPN Gama L2TP",
            VpnApp = "VPN do Windows",
            VpnCredentialId = vpnCred.Id
        };
        await repo.SaveConnectionAsync(entry);
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // 1. VPN nativa do Windows + Credencial vinculada -> aciona rasdial.exe diretamente com usuário e senha sem copiar área de transferência
        await vm.OpenVpnClientAsync();
        Assert.Single(startedProcesses);
        Assert.Equal(fakeRasdial, startedProcesses[0].FileName);
        Assert.Equal(["VPN Gama L2TP", @"GAMACORP\analista.vpn", "SYNTHETIC_VPN_PASS_99"], startedProcesses[0].ArgumentList);
        Assert.Null(lastClipboardText);

        // 2. Altera no rascunho para FortiClient (sem salvar): Conectar VPN abre FortiClient + copia senha da VPN para Ctrl+V e NÃO persiste o rascunho no SQLite
        vm.EditingEntry.VpnApp = "FortiClient";
        await vm.OpenVpnClientAsync();
        Assert.Equal(2, startedProcesses.Count);
        Assert.Equal(fakeForti, startedProcesses[1].FileName);
        Assert.Equal("SYNTHETIC_VPN_PASS_99", lastClipboardText);

        var persistedBeforeSave = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal("VPN do Windows", persistedBeforeSave.VpnApp);

        // 3. Somente Salvar persiste a edição
        Assert.True(await vm.SaveConnectionAsync());
        var persistedAfterSave = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal("FortiClient", persistedAfterSave.VpnApp);
        Assert.Equal(vpnCred.Id, persistedAfterSave.VpnCredentialId);

        // 4. Excluir credencial limpa o vínculo VpnCredentialId sem remover o acesso
        await repo.DeleteCredentialAsync(vpnCred.Id);
        var afterCredDelete = (await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.Null(afterCredDelete.VpnCredentialId);
    }

    [Fact]
    public async Task ConnectAsync_WhenServerUnreachableAndVpnConfigured_PromptsAndHandlesOpenVpnConnectAnywayOrCancel()
    {
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        settings.Load();

        var fakeMstsc = Path.Combine(_paths.RootDirectory, "fake-mstsc.exe");
        var fakeForti = Path.Combine(_paths.RootDirectory, "ProgramFiles", "Fortinet", "FortiClient", "FortiClient.exe");
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
