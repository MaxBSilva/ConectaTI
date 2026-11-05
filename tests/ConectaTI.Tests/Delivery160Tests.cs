using System.IO;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using Microsoft.Data.Sqlite;
namespace ConectaTI.Tests;

public sealed class Delivery160Tests : IDisposable
{
    private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_160_" + Guid.NewGuid().ToString("N")));
    private readonly AppLogger logger;
    private readonly SqliteRepository repo;
    private readonly CredentialVaultService vault;
    public Delivery160Tests()
    {
        logger = new(paths); repo = new(paths,logger); repo.InitializeAndMigrate();
        var settings = new SettingsService(paths,logger); settings.Load();
        vault = new(settings,repo,logger,useWindowsVault:false);
    }
    [Theory]
    [InlineData("00")] [InlineData("+0")] [InlineData(" 0")] [InlineData("0 ")] [InlineData("-0")]
    public async Task EquivalentZerosCannotBypassSecurityAtImportSaveOrGeneration(string zero)
    {
        foreach (var key in new[] { "authentication level", "enablecredsspsupport", "negotiate security layer" })
        {
            var property = key + ":i:" + zero;
            Assert.Throws<FormatException>(() => RdpFileService.Import("full address:s:rdp.example.test\n" + property,"Test"));
            var entry = new ConnectionEntry { Host="rdp.example.test" };
            entry.RdpOptions.AdditionalProperties = property;
            Assert.Throws<FormatException>(() => RdpFileService.Build(entry));
            await Assert.ThrowsAsync<FormatException>(() => new ConnectionPersistenceService(repo,vault,logger)
                .SaveAsync(entry,PasswordEditIntent.Keep,"","Test"));
            Assert.Empty(await repo.GetAllConnectionsAsync());
        }
    }
    [Theory]
    [InlineData("enablecredsspsupport:s:1")] [InlineData("authentication level:b:01")]
    [InlineData("authentication level:i:4")] [InlineData("enablecredsspsupport:i:2")]
    [InlineData("gatewayusagemethod:i:5")] [InlineData("gatewaycredentialssource:i:6")]
    [InlineData("gatewayprofileusagemethod:i:2")] [InlineData("promptcredentialonce:i:-1")]
    public void RejectsIncorrectTypesAndDomains(string property) =>
        Assert.Throws<FormatException>(() => RdpFileService.Parse(property));

    [Fact]
    public void CanonicalizesIntegersAndKeepsAllDocumentedGatewayCredentialModes()
    {
        Assert.Equal("1",RdpFileService.Parse("enablecredsspsupport:i: +01 ")["enablecredsspsupport"].Value);
        for(var mode=0;mode<=5;mode++)
        {
            var entry = new ConnectionEntry { Host="rdp.example.test" };
            entry.RdpOptions.GatewayHostname="gateway.example.test"; entry.RdpOptions.GatewayCredentialsSource=mode;
            Assert.Equal(mode.ToString(),RdpFileService.Parse(RdpFileService.Build(entry))["gatewaycredentialssource"].Value);
        }
    }
    [Theory]
    [InlineData("csv")] [InlineData("xml")] [InlineData("json")] [InlineData("package")]
    public async Task NativeFormatsPreserveCompleteEditableFieldsAcrossProtocols(string format)
    {
        var service = new ImportExportService(repo,vault,logger);
        foreach(var protocol in Enum.GetValues<ConnectionProtocol>())
        {
            var entry = new ConnectionEntry { Name="Synthetic "+protocol,Host="host.example.test",Port=protocol.GetDefaultPort(),
                ClientName="Client",GroupName="Group",Environment=EnvironmentType.Testing,Username="operator",Domain="EXAMPLE",
                PrivateKeyPath=@"C:\Fictional\key.pem",SshClient=SshClientPreference.OpenSsh,SshUseAgent=false,
                VncParameters="-viewonly -shared",VpnName="VPN",VpnApp="Client",VpnServer="vpn.example.test",VpnNotes="Notes",
                Notes="Synthetic notes; accented á",Tags="test",IsActive=false,IsFavorite=true,RdpAdminSession=true,
                RdpFullScreen=false,RdpWidth=1280,RdpHeight=720,RdpUseMultimon=false };
            entry.RdpOptions.GatewayHostname="gateway.example.test";
            entry.RdpOptions.AdditionalProperties="redirectclipboard:i:1\r\n";
            var file=Path.Combine(paths.RootDirectory,"transfer-"+protocol+"."+format);
            ConnectionEntry imported;
            if(format=="csv") { await service.ExportToCsvAsync(file,[entry]); var doc=service.ParseCsvFile(file); imported=Assert.Single(service.BuildCsvPreview(doc,doc.SuggestedMapping,[]).Items).Entry; }
            else if(format=="xml") { await service.ExportToXmlAsync(file,[entry]); imported=Assert.Single(service.ParseXmlPreview(file,[]).Items).Entry; }
            else if(format=="json") { await service.ExportToJsonAsync(file,[entry]); imported=Assert.Single(service.ParseJsonPreview(file,[]).Items).Entry; }
            else
            {
                await repo.SaveConnectionAsync(entry);
                await service.ExportTransferPackageAsync(file,[entry.Id],false);
                await service.ImportEncryptedPackageAsync(file,"",DuplicateImportHandling.CreateNew);
                imported=(await repo.GetAllConnectionsAsync()).Single(c=>c.Id!=entry.Id && c.Name==entry.Name);
            }
            Assert.True(entry.HasSameEditableValues(imported),format+" / "+protocol);
        }
    }
    [Fact]
    public void LegacyXmlAliasAndMissingFieldsUseKnownDefaults()
    {
        var service=new ImportExportService(repo,vault,logger);
        foreach(var alias in new[] { "SshClient", "SshPreference" })
        {
            var file=Path.Combine(paths.RootDirectory,"old.xml");
            File.WriteAllText(file,$"<ConectaTI><Connections><Connection><Name>Test</Name><Host>host.example.test</Host><Protocol>Ssh</Protocol><{alias}>OpenSsh</{alias}></Connection></Connections></ConectaTI>");
            var entry=Assert.Single(service.ParseXmlPreview(file,[]).Items).Entry;
            Assert.Equal(SshClientPreference.OpenSsh,entry.SshClient); Assert.True(entry.SshUseAgent); Assert.True(entry.IsActive);
        }
        var csv=Path.Combine(paths.RootDirectory,"old.csv"); File.WriteAllText(csv,"Nome;Host;Protocolo\nTest;host.example.test;Ssh");
        var doc=service.ParseCsvFile(csv); var old=Assert.Single(service.BuildCsvPreview(doc,doc.SuggestedMapping,[]).Items).Entry;
        Assert.Equal(SshClientPreference.Default,old.SshClient); Assert.True(old.SshUseAgent); Assert.True(old.IsActive);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(paths.RootDirectory,true); } catch {} }
}
