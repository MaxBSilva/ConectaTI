using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ConectaTI.Tests;

public sealed partial class ApplicationVaultTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        public AppPaths Paths { get; }=new(Path.Combine(Path.GetTempPath(),"ConectaTI_OwnVault_"+Guid.NewGuid().ToString("N")));
        public AppLogger Logger { get; }
        public SettingsService Settings { get; }
        public SqliteRepository Repository { get; }
        public CredentialVaultService Vault { get; }
        public Fixture()
        {
            Logger=new(Paths); Settings=new(Paths,Logger); Settings.Load(); Settings.Save();
            Repository=new(Paths,Logger); Repository.InitializeAndMigrate();
            Vault=new(Settings,Repository,Logger);
        }
        public void Sql(string sql)
        {
            using var db=new SqliteConnection("Data Source="+Paths.DatabasePath); db.Open();
            using var cmd=db.CreateCommand(); cmd.CommandText=sql; cmd.ExecuteNonQuery();
        }
        public void Dispose() { Vault.Lock(); SqliteConnection.ClearAllPools(); try { Directory.Delete(Paths.RootDirectory,true); } catch(IOException) {} }
    }
    private sealed class ForbiddenWindowsVault(SettingsService s,SqliteRepository r,AppLogger l)
        : CredentialVaultService(s,r,l)
    {
        public int Writes,Deletes;
        public Dictionary<string,string> Legacy { get; }=new();
        public override void WriteToWindowsVault(SavedCredential c) { Writes++; throw new NativeCredentialWriteException(8); }
        public override void RemoveFromWindowsVault(string id) { Deletes++; throw new InvalidOperationException("Forbidden native delete"); }
        protected override string? ReadLegacyWindowsEnvelope(string id) => Legacy.GetValueOrDefault(id)
            ?? throw new NativeCredentialWriteException(8);
    }
    private static string LegacyEnvelope(string password) => "DPAPI1:"+Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(password),Encoding.UTF8.GetBytes("ConectaTI::DPAPI::CurrentUser::Entropy::v1"),DataProtectionScope.CurrentUser));
    private static ImportPreviewResult Preview(int count) => new()
    {
        Items=Enumerable.Range(0,count).Select(i=>new ImportPreviewItem
        {
            RowNumber=i+1,IsValid=true,IsSelected=true,RecoveredPlainPassword="Synthetic secret #"+i,
            Entry=new ConnectionEntry { Name="Synthetic "+i,ClientName="Synthetic",Host="host-"+i+".example.test",Username="synthetic" }
        }).ToList()
    };

    [Fact]
    public async Task AutomaticProtectionNeedsNoMaster_AndReopensAfterKeyBuffersAreCleared()
    {
        using var f=new Fixture();
        var c=new SavedCredential{Name="Automatic synthetic"};
        await f.Vault.SaveApplicationCredentialAsync(c,"Synthetic secret automatic");
        Assert.False(f.Vault.IsVaultCreated); Assert.False(f.Vault.IsLocked);
        Assert.StartsWith("CTDPAPI2:",c.ProtectedSecretBase64);
        f.Vault.Lock();
        var reopened=new CredentialVaultService(f.Settings,f.Repository,f.Logger,false);
        Assert.Equal("Synthetic secret automatic",await reopened.ReadSecretAsync(c));
        Assert.False(reopened.RequiresMasterPassword);
        Assert.Equal(CredentialSecretStorage.SqliteDpapi,(await f.Repository.GetCredentialByIdAsync(c.Id))!.SecretStorage);
        var legacy=new SavedCredential{Name="Old DPAPI",SecretStorage=CredentialSecretStorage.SqliteDpapi,
            ProtectedSecretBase64=LegacyEnvelope("Synthetic legacy secret")};
        await f.Repository.SaveCredentialAsync(legacy);
        Assert.Equal("Synthetic legacy secret",await reopened.ReadSecretAsync(legacy));
    }

    private sealed class OtherContext(SettingsService s,SqliteRepository r,AppLogger l) : CredentialVaultService(s,r,l,false)
    { protected override string CurrentUserContext=>new string('A',64); }

    [Fact]
    public async Task ContextMismatchAndCorruptionAreDistinct_AndCannotDamageCopiedSecrets()
    {
        using var f=new Fixture();
        var c=new SavedCredential{Name="Source"};
        await f.Vault.SaveApplicationCredentialAsync(c,"origin secret");
        var original=c.ProtectedSecretBase64;
        var other=new OtherContext(f.Settings,f.Repository,f.Logger);
        await Assert.ThrowsAsync<CredentialUserContextException>(()=>other.ReadSecretAsync(c));
        Assert.Contains("outro usuário",other.AvailabilityMessage(c));
        Assert.Equal(original,(await f.Repository.GetCredentialByIdAsync(c.Id))!.ProtectedSecretBase64);
        var fresh=new SavedCredential{Name="New profile synthetic"};
        await other.SaveApplicationCredentialAsync(fresh,"new profile secret");
        Assert.Equal("new profile secret",await other.ReadSecretAsync(fresh));
        Assert.Equal(original,(await f.Repository.GetCredentialByIdAsync(c.Id))!.ProtectedSecretBase64);
        Assert.ThrowsAny<CryptographicException>(()=>f.Vault.UnprotectSecret(new SavedCredential
            {Id="different",ProtectedSecretBase64=original}));
        c.ProtectedSecretBase64=original[..^3]+"!!!";
        var ex=Assert.ThrowsAny<CryptographicException>(()=>f.Vault.UnprotectSecret(c));
        Assert.IsNotType<CredentialUserContextException>(ex);
        Assert.DoesNotContain("outro usuário",CredentialVaultService.RecoveryMessage(ex));
        Assert.Equal(original,(await f.Repository.GetCredentialByIdAsync(c.Id))!.ProtectedSecretBase64);
    }

    [Fact]
    public async Task PreviousMasterVaultMigrationIsAtomic_PreservesSharingAndNeverFallsBack()
    {
        using var f=new Fixture();
        await f.Vault.CreateVaultAsync(VaultTestSupport.Password,VaultTestSupport.Password);
        var first=new SavedCredential{Name="Previous shared",SecretStorage=CredentialSecretStorage.ApplicationVault};
        first.ProtectedSecretBase64=f.Vault.EncryptApplicationSecret(first.Id,"old shared secret");
        var second=new SavedCredential{Name="Previous second",SecretStorage=CredentialSecretStorage.ApplicationVault};
        second.ProtectedSecretBase64=f.Vault.EncryptApplicationSecret(second.Id,"old second secret");
        foreach(var c in new[]{first,second})await f.Repository.SaveCredentialAsync(c);
        await f.Repository.SaveConnectionsBatchAsync(new[]{new ConnectionEntry{Name="A",Host="a.example.test",CredentialId=first.Id},
            new ConnectionEntry{Name="B",Host="b.example.test",CredentialId=first.Id}});
        var metadata=f.Repository.ReadApplicationVaultMetadata();
        f.Vault.Lock();var prompts=0;
        f.Vault.RequestMigrationAsync=()=>{prompts++;return Task.FromResult(false);};
        await Assert.ThrowsAsync<CredentialMigrationRequiredException>(()=>f.Vault.ReadSecretAsync(first));
        Assert.Equal(1,prompts);
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>f.Vault.MigratePreviousVaultAsync("incorrect password"));
        using(var canceled=new CancellationTokenSource())
        {canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Vault.MigratePreviousVaultAsync(VaultTestSupport.Password,canceled.Token));}
        f.Sql("CREATE TRIGGER fail_conversion BEFORE UPDATE ON Credentials WHEN NEW.Id='"+second.Id+"' BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(()=>f.Vault.MigratePreviousVaultAsync(VaultTestSupport.Password));
        Assert.Equal(first.ProtectedSecretBase64,(await f.Repository.GetCredentialByIdAsync(first.Id))!.ProtectedSecretBase64);
        Assert.Equal(second.ProtectedSecretBase64,(await f.Repository.GetCredentialByIdAsync(second.Id))!.ProtectedSecretBase64);
        Assert.Equal(metadata,f.Repository.ReadApplicationVaultMetadata());
        f.Sql("DROP TRIGGER fail_conversion;");
        var report=await f.Vault.MigratePreviousVaultAsync(VaultTestSupport.Password);
        Assert.Equal(2,report.MigratedCount);
        var reopened=new CredentialVaultService(f.Settings,f.Repository,f.Logger,false);
        Assert.Equal("old shared secret",await reopened.ReadSecretAsync(first));
        Assert.Equal("old second secret",await reopened.ReadSecretAsync(second));
        Assert.All(await f.Repository.GetAllConnectionsAsync(),e=>Assert.Equal(first.Id,e.CredentialId));
        var migrated=(await f.Repository.GetCredentialByIdAsync(first.Id))!;
        await reopened.SaveApplicationCredentialAsync(migrated,"replacement secret");
        Assert.Equal("replacement secret",await reopened.ReadSecretAsync(first));
        await reopened.DeleteApplicationCredentialAsync(first.Id);
        Assert.Empty(await reopened.ReadSecretAsync(first));
        Assert.Equal(metadata,f.Repository.ReadApplicationVaultMetadata());
    }

    [Theory]
    [InlineData(225)]
    [InlineData(633)]
    [InlineData(1000)]
    public async Task RealApplicationVaultImportsAndReopensEverySecret_WithoutWindowsCopies(int count)
    {
        using var f=new Fixture();
        // Normal production service, not a simulated storage implementation.
        var service=new ImportExportService(f.Repository,f.Vault,f.Logger);
        var clock=Stopwatch.StartNew(); using var process=Process.GetCurrentProcess(); var before=process.WorkingSet64;
        var report=await service.CommitImportAsync(Preview(count),DuplicateImportHandling.Ignore);
        var ms=clock.Elapsed.TotalMilliseconds; process.Refresh();
        Assert.Equal(count,report.InsertedCount); Assert.Equal(count,report.CredentialsImportedCount);
        var reopened=new CredentialVaultService(f.Settings,f.Repository,f.Logger,false);
        var rows=await f.Repository.GetAllConnectionsAsync();
        foreach(var row in rows)
        {
            var c=(await f.Repository.GetCredentialByIdAsync(row.CredentialId!))!;
            Assert.Equal(CredentialSecretStorage.SqliteDpapi,c.SecretStorage);
            Assert.Equal("Synthetic secret #"+row.Name["Synthetic ".Length..],await reopened.ReadSecretAsync(c));
        }
        reopened.Lock();
        using var db=new SqliteConnection("Data Source="+f.Paths.DatabasePath); db.Open();
        using var command=db.CreateCommand(); command.CommandText="PRAGMA integrity_check";
        Assert.Equal("ok",command.ExecuteScalar());
        var metric=JsonSerializer.Serialize(new {count,commitMs=ms,beforeWorkingSet=before,afterWorkingSet=process.WorkingSet64,
            storage="real CurrentUser DPAPI; no Credential Manager writes",runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription});
        output.WriteLine(metric);
        var metricPath=Environment.GetEnvironmentVariable("CONECTATI_VAULT_METRICS");
        if(!string.IsNullOrEmpty(metricPath)) await File.AppendAllTextAsync(metricPath,metric+Environment.NewLine);
    }

    [Fact]
    public async Task NativeErrorEightCannotPreventApplicationImportSaveOrRemoval()
    {
        using var f=new Fixture(); var vault=new ForbiddenWindowsVault(f.Settings,f.Repository,f.Logger);
        var service=new ImportExportService(f.Repository,vault,f.Logger);
        var report=await service.CommitImportAsync(Preview(633),DuplicateImportHandling.Ignore);
        Assert.Equal(633,report.InsertedCount);
        var entry=(await f.Repository.GetAllConnectionsAsync())[0];
        await new ConnectionPersistenceService(f.Repository,vault,f.Logger).SaveAsync(entry,PasswordEditIntent.Replace,"replacement","Synthetic");
        var saved=(await f.Repository.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal("replacement",await vault.ReadSecretAsync((await f.Repository.GetCredentialByIdAsync(saved.CredentialId!))!));
        await new ConnectionPersistenceService(f.Repository,vault,f.Logger).SaveAsync(saved,PasswordEditIntent.Remove,"","");
        Assert.Null((await f.Repository.GetConnectionByIdAsync(entry.Id))!.CredentialId);
        Assert.Equal(0,vault.Writes); Assert.Equal(0,vault.Deletes);
    }

    [Fact]
    public async Task LegacyMigrationPreservesSharedIdsAndOriginals_ReportsReadFailures()
    {
        using var f=new Fixture(); var vault=new ForbiddenWindowsVault(f.Settings,f.Repository,f.Logger);
        var windows=new SavedCredential {Name="Windows synthetic",SecretStorage=CredentialSecretStorage.LegacyWindows};
        var dpapi=new SavedCredential {Name="DPAPI synthetic",SecretStorage=CredentialSecretStorage.SqliteDpapi,ProtectedSecretBase64=LegacyEnvelope("dpapi secret")};
        var broken=new SavedCredential {Name="Broken synthetic",SecretStorage=CredentialSecretStorage.SqliteDpapi,ProtectedSecretBase64="invalid"};
        var oldWindows=LegacyEnvelope("windows secret"); vault.Legacy.Add(windows.Id,oldWindows);
        foreach(var c in new[]{windows,dpapi,broken}) await f.Repository.SaveCredentialAsync(c);
        await f.Repository.SaveConnectionsBatchAsync(new[]{new ConnectionEntry{Name="A",Host="a.example.test",CredentialId=windows.Id},
            new ConnectionEntry{Name="B",Host="b.example.test",CredentialId=windows.Id}});
        var report=await vault.MigrateLegacyCredentialsAsync();
        Assert.Equal(2,report.MigratedCount); Assert.Equal(broken.Id,Assert.Single(report.Pending).CredentialId);
        Assert.Equal(oldWindows,vault.Legacy[windows.Id]); Assert.Equal(0,vault.Deletes);
        var migrated=(await f.Repository.GetCredentialByIdAsync(windows.Id))!;
        Assert.Equal(CredentialSecretStorage.SqliteDpapi,migrated.SecretStorage);
        Assert.Equal("windows secret",await vault.ReadSecretAsync(migrated));
        Assert.All(await f.Repository.GetAllConnectionsAsync(),c=>Assert.Equal(windows.Id,c.CredentialId));
        Assert.Equal("dpapi secret",await vault.ReadSecretAsync(dpapi));
        await vault.SaveApplicationCredentialAsync(migrated,"new own secret");
        await vault.MigrateLegacyCredentialsAsync();
        Assert.Equal("new own secret",await vault.ReadSecretAsync(windows));
        await vault.DeleteApplicationCredentialAsync(windows.Id);
        Assert.Empty(await vault.ReadSecretAsync(windows));
        Assert.Equal(oldWindows,vault.Legacy[windows.Id]);
        Assert.Equal("invalid",(await f.Repository.GetCredentialByIdAsync(broken.Id))!.ProtectedSecretBase64);
    }

    [Fact]
    public async Task LegacyMigrationSqlFailureRollsBackAndPreservesOriginalEnvelope()
    {
        using var f=new Fixture(); await f.Vault.CreateVaultAsync(VaultTestSupport.Password,VaultTestSupport.Password);
        var c=new SavedCredential {Name="Legacy",SecretStorage=CredentialSecretStorage.SqliteDpapi,ProtectedSecretBase64=LegacyEnvelope("legacy secret")};
        await f.Repository.SaveCredentialAsync(c);
        f.Sql("CREATE TRIGGER fail_migration BEFORE UPDATE ON Credentials WHEN NEW.SecretStorage=1 BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(()=>f.Vault.MigrateLegacyCredentialsAsync());
        var preserved=(await f.Repository.GetCredentialByIdAsync(c.Id))!;
        Assert.Equal(c.ProtectedSecretBase64,preserved.ProtectedSecretBase64);
        Assert.Equal(CredentialSecretStorage.SqliteDpapi,preserved.SecretStorage);
    }

    [Fact]
    public async Task VersionFiveDatabaseGetsBackedUpAndMigratedWithoutLosingLegacySecrets()
    {
        using var f=new Fixture();
        var credential=new SavedCredential{Name="Legacy schema",SecretStorage=CredentialSecretStorage.SqliteDpapi,
            ProtectedSecretBase64=LegacyEnvelope("schema legacy secret")};
        await f.Repository.SaveCredentialAsync(credential);
        f.Sql("""
            ALTER TABLE Credentials DROP COLUMN LegacySecretStorage;
            ALTER TABLE Credentials DROP COLUMN LegacyProtectedSecretBase64;
            ALTER TABLE Credentials DROP COLUMN SecretStorage;
            ALTER TABLE PendingVaultCleanup DROP COLUMN VerifiedSqliteCopy;
            DROP TABLE AppVaultMetadata;
            DROP TABLE AppVaultNonces;
            DROP TABLE CredentialProtectionHistory;
            DELETE FROM SchemaMigrations WHERE Version>=6;
            """);
        f.Repository.InitializeAndMigrate();
        Assert.Equal(8,f.Repository.GetSchemaVersion());
        Assert.Single(Directory.GetFiles(f.Paths.BackupsDirectory,"pre-migration-v5-*.db"));
        var migrated=(await f.Repository.GetCredentialByIdAsync(credential.Id))!;
        Assert.Equal(credential.ProtectedSecretBase64,migrated.ProtectedSecretBase64);
        Assert.Equal(CredentialSecretStorage.SqliteDpapi,migrated.SecretStorage);
        Assert.False(f.Vault.IsVaultCreated);
        Assert.Equal(1,(await f.Vault.MigrateLegacyCredentialsAsync()).MigratedCount);
        Assert.Equal("schema legacy secret",await f.Vault.ReadSecretAsync(migrated));
    }

    [Fact]
    public async Task RealLegacyWindowsCredentialMigratesButItsOriginalRemains()
    {
        using var f=new Fixture();
        await f.Vault.CreateVaultAsync(VaultTestSupport.Password,VaultTestSupport.Password);
        var credential=new SavedCredential{Name="Unique native legacy test",SecretStorage=CredentialSecretStorage.LegacyWindows};
        var target="ConectaTI:Credential:"+credential.Id;
        var original=LegacyEnvelope("native legacy synthetic secret");
        var created=false;
        try
        {
            created=WindowsCredentialVaultInterop.WriteGenericSecret(target,"synthetic",original);
            Assert.True(created);
            await f.Repository.SaveCredentialAsync(credential);
            Assert.Equal("native legacy synthetic secret",await f.Vault.ReadSecretAsync(credential));
            var report=await f.Vault.MigrateLegacyCredentialsAsync();
            Assert.Equal(1,report.MigratedCount); Assert.Empty(report.Pending);
            Assert.Equal(original,WindowsCredentialVaultInterop.ReadGenericSecret(target));
            Assert.Equal("native legacy synthetic secret",await f.Vault.ReadSecretAsync(credential));
            await f.Vault.DeleteApplicationCredentialAsync(credential.Id);
            Assert.Equal(original,WindowsCredentialVaultInterop.ReadGenericSecret(target));
            Assert.Empty(await f.Vault.ReadSecretAsync(credential));
        }
        finally { if(created) WindowsCredentialVaultInterop.DeleteGenericSecret(target); }
    }

    [Fact]
    public void StructuredDiagnosticsPreserveTechnicalFieldsWhenSecretsAreShort()
    {
        using var f=new Fixture();
        using(f.Logger.Redact(new[]{"a","8","x","."}))
        {
            try { throw new NativeCredentialWriteException(8); }
            catch(Exception e) { f.Logger.RecordFailure("Import.Credentials.WriteToWindowsVault",e,49335,225,TimeSpan.FromSeconds(1),7,7,7); }
            Assert.Equal(typeof(NativeCredentialWriteException).FullName,f.Logger.LastFailure!.ExceptionType);
            Assert.Equal("Import.Credentials.WriteToWindowsVault",f.Logger.LastFailure.Stage);
            Assert.Contains(nameof(StructuredDiagnosticsPreserveTechnicalFieldsWhenSecretsAreShort),f.Logger.LastFailure.StackTrace);
            Assert.DoesNotContain("[REDACTED]",f.Logger.LastFailure.StackTrace);
            Assert.Equal(8,f.Logger.LastFailure.NativeErrorCode);
            Assert.Equal("CredWriteW",f.Logger.LastFailure.NativeApi);
            try { throw new IOException("password=a token=x"); }
            catch(Exception e) { f.Logger.RecordFailure("Credentials.Read",e); }
            Assert.Equal("[REDACTED: sensitive message field]",f.Logger.LastFailure.Message);
            Assert.Contains("System.IO.IOException",f.Logger.LastFailure.ExceptionType);
        }
    }
}
