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

public sealed class ActivityStatusTests : IDisposable
{
    private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI-Activity-" + Guid.NewGuid().ToString("N")));
    private readonly AppLogger logger;
    private readonly SqliteRepository repo;
    private readonly SettingsService settings;
    private readonly CredentialVaultService vault;
    private readonly ImportExportService transfer;
    private readonly ClipboardService clipboard;
    private string? copied;
    public ActivityStatusTests()
    {
        ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        logger = new(paths); repo = new(paths, logger); repo.InitializeAndMigrate();
        settings = new(paths, logger); settings.Load(); vault = new(settings, repo, logger, useWindowsVault:false);
        transfer = new(repo, vault, logger);
        clipboard = new(logger, text => { copied = text; return true; }, () => copied, () => copied = null);
    }
    private MainViewModel Vm() => new(paths, logger, settings, repo, vault, clipboard,
        new(paths, settings, repo, vault, clipboard, logger), transfer, new NetworkDiagnosticService());
    [Fact]
    public async Task SaveReopenReactivateKeepsSharedCredentialAndDrafts()
    {
        var a = new ConnectionEntry { Name="Avícola A", Host="a.example.test" };
        var saved = await new ConnectionPersistenceService(repo,vault,logger).SaveAsync(a, PasswordEditIntent.Replace,"SYNTHETIC_ACTIVITY_SECRET","Fictício");
        var b = new ConnectionEntry { Name="Avícola B", Host="b.example.test", CredentialId=saved.Entry.CredentialId };
        await repo.SaveConnectionAsync(b);
        var vm=Vm(); await vm.InitializeAsync(seedDemoIfEmpty:false); vm.SelectedConnection=saved.Entry;
        Assert.True(vm.EditingEntry.IsActive); vm.EditingEntry.IsActive=false; Assert.True(vm.CanSave);
        Assert.True((await repo.GetConnectionByIdAsync(a.Id))!.IsActive);
        vm.SelectedConnection=b; Assert.True(vm.EditingEntry.IsActive); vm.SelectedConnection=saved.Entry;
        Assert.False(vm.EditingEntry.IsActive); Assert.True(await vm.SaveConnectionAsync());
        var reopened=Vm(); await reopened.InitializeAsync(seedDemoIfEmpty:false);
        Assert.False(reopened.EditingEntry.IsActive); Assert.Equal("Inativo",reopened.EditingEntry.ActivityDisplay);
        Assert.Equal(saved.Entry.CredentialId,reopened.EditingEntry.CredentialId);
        await reopened.CopyAllDataAsync(); Assert.Contains("Status: Inativo",copied); Assert.Contains("SYNTHETIC_ACTIVITY_SECRET",copied);
        reopened.SearchText="avicola"; Assert.Equal(2,reopened.FilteredConnections.Count);
        Assert.False(reopened.FilteredConnections[0].IsActive); Assert.True(reopened.FilteredConnections[1].IsActive);
        reopened.EditingEntry.IsActive=true; Assert.True(reopened.CanSave); Assert.True(await reopened.SaveConnectionAsync());
        Assert.True((await repo.GetConnectionByIdAsync(a.Id))!.IsActive);
        Assert.Equal("SYNTHETIC_ACTIVITY_SECRET",await vault.ReadSecretAsync((await repo.GetCredentialByIdAsync(saved.Entry.CredentialId!))!));
        Assert.Equal(saved.Entry.CredentialId,(await repo.GetConnectionByIdAsync(b.Id))!.CredentialId);
    }
    [Fact]
    public async Task VersionNineMigrationDefaultsExistingRecordsToActiveAndPreservesCredentials()
    {
        var saved=await new ConnectionPersistenceService(repo,vault,logger).SaveAsync(new() {Name="Legacy",Host="legacy.example.test"}, PasswordEditIntent.Replace,"SYNTHETIC_LEGACY","Test");
        using(var connection=new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            connection.Open(); using var command=connection.CreateCommand();
            command.CommandText="ALTER TABLE Connections DROP COLUMN IsActive; DELETE FROM SchemaMigrations WHERE Version>=10;"; command.ExecuteNonQuery();
        }
        var reopened=new SqliteRepository(paths,logger); reopened.InitializeAndMigrate();
        Assert.Equal(12,reopened.GetSchemaVersion()); var loaded=(await reopened.GetConnectionByIdAsync(saved.Entry.Id))!;
        Assert.True(loaded.IsActive); Assert.Equal(saved.Entry.CredentialId,loaded.CredentialId);
        Assert.Single(Directory.GetFiles(paths.BackupsDirectory,"pre-migration-v9-*.db"));
        Assert.True(new ConnectionEntry().IsActive); Assert.True(new ConnectionEntry().CreateDuplicate().IsActive);
        loaded.IsActive=false; Assert.False(loaded.Clone().IsActive); Assert.False(loaded.CreateDuplicate().IsActive);
    }
    [Fact]
    public async Task BackupRestoreAndTrashAreIndependent()
    {
        var entry=new ConnectionEntry {Name="Inactive",Host="inactive.example.test",IsActive=false}; await repo.SaveConnectionAsync(entry);
        var backup=await repo.CreateBackupAsync(); entry.IsActive=true; await repo.SaveConnectionAsync(entry);
        await repo.RestoreBackupAsync(backup); var loaded=(await repo.GetConnectionByIdAsync(entry.Id))!;
        Assert.False(loaded.IsActive); Assert.False(loaded.IsDeleted);
        loaded.IsDeleted=true; await repo.SaveConnectionAsync(loaded); await repo.RestoreConnectionAsync(loaded.Id);
        Assert.False((await repo.GetConnectionByIdAsync(loaded.Id))!.IsActive);
    }
    [Theory]
    [InlineData("csv")] [InlineData("xml")] [InlineData("json")]
    public async Task NativeFormatsRoundTripAndOldFormatsDefaultToActive(string format)
    {
        var entry=new ConnectionEntry {Name="Inactive",Host="inactive.example.test",IsActive=false};
        var file=Path.Combine(paths.RootDirectory,"status."+format); ImportPreviewResult preview;
        if(format=="csv") {await transfer.ExportToCsvAsync(file,[entry]); var doc=transfer.ParseCsvFile(file); preview=transfer.BuildCsvPreview(doc,doc.SuggestedMapping,[]);}
        else if(format=="xml") {await transfer.ExportToXmlAsync(file,[entry]);preview=transfer.ParseXmlPreview(file,[]);}
        else {await transfer.ExportToJsonAsync(file,[entry]);preview=transfer.ParseJsonPreview(file,[]);}
        Assert.False(Assert.Single(preview.Items).Entry.IsActive);
        if (format == "xml")
        {
            var document = System.Xml.Linq.XDocument.Load(file);
            foreach (var element in document.Descendants("IsActive").ToArray()) element.Remove();
            document.Save(file);
            Assert.True(Assert.Single(transfer.ParseXmlPreview(file,[]).Items).Entry.IsActive);
        }
        var csv=transfer.ParseCsvContent("Nome;Host\nLegacy;legacy.example.test");
        Assert.True(Assert.Single(transfer.BuildCsvPreview(csv,csv.SuggestedMapping,[]).Items).Entry.IsActive);
        Assert.True(Assert.Single(transfer.ParseJsonContentPreview("[{\"Name\":\"Legacy\",\"Host\":\"legacy.example.test\"}]",[]).Items).Entry.IsActive);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransferPackageRetainsInactiveAndSavedSecret(bool includePasswords)
    {
        var saved=await new ConnectionPersistenceService(repo,vault,logger).SaveAsync(new(){Name="Inactive",Host="inactive.example.test",IsActive=false},PasswordEditIntent.Replace,"SYNTHETIC_TRANSFER","Test");
        var file=Path.Combine(paths.RootDirectory,includePasswords?"test.ctienc":"test.ctishare");
        await transfer.ExportTransferPackageAsync(file,[saved.Entry.Id],includePasswords,"Synthetic transport","Synthetic transport");
        var preview=await transfer.ReadEncryptedPreviewAsync(file,includePasswords?"Synthetic transport":"");
        Assert.False(Assert.Single(preview.Items).Entry.IsActive);
        var result=await transfer.ImportEncryptedPackageAsync(file,includePasswords?"Synthetic transport":"",DuplicateImportHandling.CreateNew);
        Assert.Equal(1,result.ConnectionsImported);
        Assert.All(await repo.GetAllConnectionsAsync(),entry=>Assert.False(entry.IsActive));
    }
    [Fact]
    public async Task InactiveLaunchStillAllowedAndRdpPropertiesUnchanged()
    {
        var entry=new ConnectionEntry {Name="Inactive",Host="inactive.example.test",IsActive=false}; await repo.SaveConnectionAsync(entry);
        var started=false; var launcher=new ConnectionLauncherService(paths,settings,repo,vault,clipboard,logger,_=>{started=true;return Environment.ProcessId;});
        var active=entry.Clone(); active.IsActive=true;
        Assert.Equal(launcher.BuildLaunchPlan(active).TempRdpContent,launcher.BuildLaunchPlan(entry).TempRdpContent);
        var result=await launcher.LaunchAsync(entry); Assert.True(result.Started); Assert.True(started);
    }
    public void Dispose() {SqliteConnection.ClearAllPools();try{Directory.Delete(paths.RootDirectory,true);}catch{}}
}

