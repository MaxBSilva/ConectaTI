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

public sealed class CopyAllTests : IDisposable
{
    private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Copy_" + Guid.NewGuid().ToString("N")));
    private readonly AppLogger logger;
    private readonly SettingsService settings;
    private readonly SqliteRepository repo;
    private readonly CredentialVaultService vault;
    private string? copied;
    public CopyAllTests()
    {
        ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        logger = new(paths); settings = new(paths, logger); settings.Load(); repo = new(paths, logger); repo.InitializeAndMigrate();
        vault = new(settings, repo, logger, useWindowsVault: false);
    }
    private MainViewModel Vm()
    {
        var clipboard = new ClipboardService(logger, text => { copied = text; return true; }, () => copied, () => copied = null);
        return new(paths, logger, settings, repo, vault, clipboard, new(paths, settings, repo, vault, clipboard, logger), new(repo, vault, logger), new NetworkDiagnosticService());
    }
    [Fact]
    public async Task CopyUsesUnsavedFormAndSecretThenSwitchesWithoutStaleData()
    {
        var a = new ConnectionEntry { Name = "Alpha", Host = "alpha.example.test" };
        var b = new ConnectionEntry { Name = "Beta", Host = "beta.example.test", GroupName = "", Environment = EnvironmentType.Unspecified };
        await repo.SaveConnectionsBatchAsync([a,b]);
        var vm = Vm(); await vm.InitializeAsync(seedDemoIfEmpty:false); vm.SelectedConnection = a;
        vm.EditingEntry.Name = "Unsaved Alpha"; vm.InlineHostAndPort = "draft.example.test:3390";
        vm.InlineUserWithDomain = "EXAMPLE\\draft"; vm.InlinePasswordInput = "SYNTHETIC_COPY_SECRET";
        vm.EditingEntry.VpnServer = "vpn.example.test"; vm.EditingEntry.RdpOptions.GatewayHostname = "gateway.example.test";
        vm.EditingEntry.RdpOptions.GatewayCredentialsSource = 0; vm.EditingEntry.RdpOptions.GatewayProfileUsageMethod = 0;
        await vm.CopyAllDataAsync();
        Assert.Contains("Unsaved Alpha", copied); Assert.Contains("draft.example.test:3390", copied); Assert.Contains("Porta: 3390", copied);
        Assert.Contains("Senha: SYNTHETIC_COPY_SECRET", copied); Assert.Contains("Servidor VPN: vpn.example.test", copied);
        Assert.Contains("Origem das credenciais: Senha (0)", copied); Assert.Contains("Perfil do gateway: Perfil padrão (0)", copied);
        Assert.Contains("Modo administrativo: Não", copied); Assert.DoesNotContain("Resolução:", copied);
        Assert.DoesNotContain("Observações:", copied); Assert.Equal("Dados do acesso copiados", vm.StatusMessage);
        Assert.Equal("Alpha", (await repo.GetConnectionByIdAsync(a.Id))!.Name);
        vm.SelectedConnection = b; await vm.CopyAllDataAsync();
        Assert.Contains("Beta", copied); Assert.DoesNotContain("Alpha", copied); Assert.DoesNotContain("Senha:", copied);
        Assert.DoesNotContain("Gateway", copied); Assert.DoesNotContain("VPN", copied); Assert.DoesNotContain("Ambiente:", copied);
        foreach (var file in Directory.GetFiles(paths.LogsDirectory)) Assert.DoesNotContain("SYNTHETIC_COPY_SECRET", File.ReadAllText(file));
    }
    [Fact]
    public async Task SavedSecretIsRecoveredAndPendingRemovalDoesNotCopyIt()
    {
        var entry = new ConnectionEntry { Name = "Saved", Host = "saved.example.test" };
        var saved = await new ConnectionPersistenceService(repo,vault,logger).SaveAsync(entry,PasswordEditIntent.Replace,"SYNTHETIC_SAVED_SECRET","Test");
        var vm = Vm(); await vm.InitializeAsync(seedDemoIfEmpty:false);
        await vm.CopyAllDataAsync(); Assert.Contains("Senha: SYNTHETIC_SAVED_SECRET",copied);
        vm.MarkPasswordForRemoval(); await vm.CopyAllDataAsync(); Assert.DoesNotContain("Senha:", copied);
        var diagnostic = await vm.BuildRdpPreviewAsync(vm.EditingEntry.Clone()); Assert.DoesNotContain("SYNTHETIC_SAVED_SECRET", diagnostic);
    }
    [Theory]
    [InlineData(ConnectionProtocol.Ssh, false, false)]
    [InlineData(ConnectionProtocol.Rdp, true, false)]
    [InlineData(ConnectionProtocol.Rdp, false, true)]
    public void OmitsInactiveOptions(ConnectionProtocol protocol, bool fullScreen, bool multimon)
    {
        var entry = new ConnectionEntry { Protocol = protocol, RdpFullScreen = fullScreen, RdpUseMultimon = multimon };
        var text = ConnectionCopyFormatter.Format(entry,"host.example.test", "", "");
        Assert.DoesNotContain("Resolução:",text); Assert.DoesNotContain("Senha:",text);
        if (protocol == ConnectionProtocol.Ssh) Assert.DoesNotContain("Tela inteira",text);
        entry.RdpOptions.GatewayHostname = "disabled.example.test"; entry.RdpOptions.GatewayUsageMethod=0;
        Assert.DoesNotContain("Gateway RDP",ConnectionCopyFormatter.Format(entry,"host.example.test","",""));
    }
    [Theory]
    [InlineData(11)] [InlineData(12.5)] [InlineData(18)]
    public void FontPreferencePersists(double size)
    {
        settings.Current.FontSize=size; settings.Save();
        Assert.Equal(size,new SettingsService(paths,logger).Load().FontSize);
        settings.Current.FontSize=12.5; ViewPreferencesService.Normalize(settings.Current); settings.Save();
        Assert.Equal(12.5,new SettingsService(paths,logger).Load().FontSize);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(paths.RootDirectory,true); } catch {} }
}


