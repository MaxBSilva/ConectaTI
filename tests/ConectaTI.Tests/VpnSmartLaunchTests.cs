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
    public void VpnLauncher_ResolvesMessyRealWorldSpellings_CustomExe_StartMenuShortcuts_AndWindowsFallback()
    {
        var fakeProgFiles = Path.Combine(_paths.RootDirectory, "ProgramFiles");
        var fakeStartMenu = Path.Combine(_paths.RootDirectory, "StartMenu");

        var fortiExe = Path.Combine(fakeProgFiles, "Fortinet", "FortiClient", "FortiClient.exe");
        var openConnectExe = Path.Combine(fakeProgFiles, "OpenConnect-GUI", "openconnect-gui.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fortiExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(openConnectExe)!);
        File.WriteAllText(fortiExe, "MZ-fictitious");
        File.WriteAllText(openConnectExe, "MZ-fictitious");

        var sonicDir = Path.Combine(fakeStartMenu, "SonicWall");
        Directory.CreateDirectory(sonicDir);
        var uninstallLnk = Path.Combine(sonicDir, "Uninstall NetExtender.lnk");
        var realLnk = Path.Combine(sonicDir, "SonicWall NetExtender.lnk");
        File.WriteAllText(uninstallLnk, "shortcut");
        File.WriteAllText(realLnk, "shortcut");

        var launchedInfos = new List<ProcessStartInfo>();
        var service = new VpnClientLauncherService(
            customStarter: psi => { launchedInfos.Add(psi); return true; },
            customProgramRoots: [fakeProgFiles],
            customStartMenuRoots: [fakeStartMenu]);

        // 1. "fortcliente" (grafia comum com erro de digitação) -> FortiClient.exe
        var fortiRes = service.Resolve(new ConnectionEntry { VpnApp = "fortcliente", VpnServer = "vpn.cliente1.example.test:10443" });
        Assert.True(fortiRes.Found);
        Assert.Equal("FortiClient", fortiRes.DisplayClientName);
        Assert.Equal(fortiExe, fortiRes.TargetPathOrUri);

        // 2. "open connect" -> openconnect-gui.exe
        var ocRes = service.Resolve(new ConnectionEntry { VpnApp = "open connect" });
        Assert.True(ocRes.Found);
        Assert.Equal("OpenConnect GUI", ocRes.DisplayClientName);
        Assert.Equal(openConnectExe, ocRes.TargetPathOrUri);

        // 3. "open gui" -> quando OpenVPN não está instalado mas OpenConnect GUI está, resolve OpenConnect GUI
        var ogRes = service.Resolve(new ConnectionEntry { VpnApp = "open gui" });
        Assert.True(ogRes.Found);
        Assert.Equal(openConnectExe, ogRes.TargetPathOrUri);

        // 4. VpnApp vazio, mas o analista escreveu "Usar FortiClient" em VpnName/VpnNotes
        var notesFallbackRes = service.Resolve(new ConnectionEntry { VpnName = "VPN Matriz", VpnNotes = "Conectar pelo fortclient antes do Sapiens" });
        Assert.True(notesFallbackRes.Found);
        Assert.Equal(fortiExe, notesFallbackRes.TargetPathOrUri);

        // 5. Atalho no Menu Iniciar ignorando "Uninstall"
        var lnkRes = service.Resolve(new ConnectionEntry { VpnApp = "net extender" });
        Assert.True(lnkRes.Found);
        Assert.Equal(realLnk, lnkRes.TargetPathOrUri);
        Assert.True(lnkRes.UseShellExecute);

        // 6. VpnApp totalmente vazio e sem vendor conhecido -> fallback para VPN nativa do Windows
        var winFallbackRes = service.Resolve(new ConnectionEntry { VpnName = "VPN L2TP Ficticia" });
        Assert.True(winFallbackRes.Found);
        Assert.True(winFallbackRes.IsWindowsNativeFallback);

        var outcome = service.Launch(new ConnectionEntry { VpnApp = "fortcliente" });
        Assert.True(outcome.Started);
        Assert.Single(launchedInfos);
        Assert.Equal(fortiExe, launchedInfos[0].FileName);
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

        // Caso 1: Servidor inacessível -> Analista escolhe "Abrir VPN" -> Abre FortiClient e NÃO abre MSTSC
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
