using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed partial class ApplicationVaultTests
{
    private static async Task<(SavedCredential Credential,ConnectionEntry A,ConnectionEntry B,ConnectionEntry Excluded)> SeedTransfer(Fixture f)
    {
        var shared=new SavedCredential{Name="Selected shared",Username="synthetic"};
        await f.Vault.SaveApplicationCredentialAsync(shared,"SELECTED synthetic secret");
        var excluded=new SavedCredential{Name="Excluded credential"};
        await f.Vault.SaveApplicationCredentialAsync(excluded,"EXCLUDED synthetic secret");
        var orphan=new SavedCredential{Name="UNLINKED credential"};
        await f.Vault.SaveApplicationCredentialAsync(orphan,"UNLINKED synthetic secret");
        var a=new ConnectionEntry{Name="Selected A",ClientName="Client One",GroupName="Group A",Host="a.example.test",CredentialId=shared.Id};
        var b=new ConnectionEntry{Name="Selected B",ClientName="Client Two",GroupName="Group B",Host="b.example.test",CredentialId=shared.Id};
        var no=new ConnectionEntry{Name="EXCLUDED access",Host="excluded.example.test",CredentialId=excluded.Id};
        var trash=new ConnectionEntry{Name="TRASH access",Host="trash.example.test",CredentialId=excluded.Id,IsDeleted=true};
        await f.Repository.SaveConnectionsBatchAsync(new[]{a,b,no,trash});
        return(shared,a,b,no);
    }

    [Fact]
    public async Task SelectivePackageContainsOnlySelectedMetadataAndOneSharedCredential()
    {
        using var source=new Fixture();
        var seed=await SeedTransfer(source);
        var path=Path.Combine(source.Paths.RootDirectory,"selected.ctienc");
        var service=new ImportExportService(source.Repository,source.Vault,source.Logger);
        await service.ExportTransferPackageAsync(path,new[]{seed.A.Id,seed.B.Id},true,"Synthetic transport","Synthetic transport");
        var file=await File.ReadAllBytesAsync(path);
        Assert.True(file.AsSpan().StartsWith("CTIENC02"u8));
        var plain=CredentialVaultService.DecryptPortablePayload(file,"Synthetic transport");
        try
        {
            var text=Encoding.UTF8.GetString(plain);
            foreach(var excluded in new[]{"EXCLUDED","UNLINKED","TRASH","AppVaultMetadata","WrappedKey","CTDPAPI2:"})
                Assert.DoesNotContain(excluded,text);
            var bundle=JsonSerializer.Deserialize<PortableEncryptedBundle>(plain)!;
            Assert.Equal("ConectaTI-Portable-2.0",bundle.FormatVersion);
            Assert.Equal(2,bundle.Connections.Count);
            Assert.Equal(seed.Credential.Id,Assert.Single(bundle.Credentials).Id);
            Assert.All(bundle.Connections,c=>Assert.Equal(seed.Credential.Id,c.CredentialId));
        }
        finally {CryptographicOperations.ZeroMemory(plain);}
        Assert.DoesNotContain("SELECTED synthetic secret",Encoding.Latin1.GetString(file));
        Assert.DoesNotContain("Synthetic transport",await File.ReadAllTextAsync(source.Paths.SettingsPath));
    }

    [Fact]
    public async Task MetadataOnlyPackageHasNoSecretsAndImportsWithoutPassword()
    {
        using var source=new Fixture();using var target=new Fixture();
        var seed=await SeedTransfer(source);
        var path=Path.Combine(source.Paths.RootDirectory,"metadata.ctishare");
        await new ImportExportService(source.Repository,source.Vault,source.Logger)
            .ExportTransferPackageAsync(path,new[]{seed.A.Id},false);
        var file=await File.ReadAllBytesAsync(path);
        Assert.True(file.AsSpan().StartsWith("CTIMETA2"u8));
        var text=Encoding.UTF8.GetString(file);
        Assert.DoesNotContain("synthetic secret",text);
        Assert.DoesNotContain(seed.Credential.Id,text);
        var importer=new ImportExportService(target.Repository,target.Vault,target.Logger);
        var result=await importer.ImportEncryptedPackageAsync(path,"",DuplicateImportHandling.Ignore);
        Assert.Equal(1,result.ConnectionsImported);Assert.Equal(0,result.CredentialsImported);
        Assert.Null(Assert.Single(await target.Repository.GetAllConnectionsAsync()).CredentialId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongPasswordOrTamperCannotModifyDestination(bool tamper)
    {
        using var source=new Fixture();using var target=new Fixture();
        var seed=await SeedTransfer(source);
        var path=Path.Combine(source.Paths.RootDirectory,"protected.ctienc");
        await new ImportExportService(source.Repository,source.Vault,source.Logger)
            .ExportTransferPackageAsync(path,new[]{seed.A.Id},true,"Transport correct","Transport correct");
        if(tamper){var bytes=await File.ReadAllBytesAsync(path);bytes[^1]^=1;await File.WriteAllBytesAsync(path,bytes);}
        var importer=new ImportExportService(target.Repository,target.Vault,target.Logger);
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>importer.ImportEncryptedPackageAsync(path,
            tamper?"Transport correct":"Incorrect password",DuplicateImportHandling.Ignore));
        Assert.Empty(await target.Repository.GetAllConnectionsAsync());Assert.Empty(await target.Repository.GetAllCredentialsAsync());
    }

    [Fact]
    public async Task SharedTransferReprotectsForDestinationContext_AndDuplicatesPreserveLinks()
    {
        using var source=new Fixture();using var target=new Fixture();
        var seed=await SeedTransfer(source);
        var path=Path.Combine(source.Paths.RootDirectory,"shared.ctienc");
        await new ImportExportService(source.Repository,source.Vault,source.Logger)
            .ExportTransferPackageAsync(path,new[]{seed.A.Id,seed.B.Id},true,"Synthetic transport","Synthetic transport");
        // Recorded SID mismatch is simulated; DPAPI itself still runs under this test process's real Windows account.
        var context=new OtherContext(target.Settings,target.Repository,target.Logger);
        var importer=new ImportExportService(target.Repository,context,target.Logger);
        await importer.ImportEncryptedPackageAsync(path,"Synthetic transport",DuplicateImportHandling.Ignore);
        var c=Assert.Single(await target.Repository.GetAllCredentialsAsync());
        Assert.NotEqual(seed.Credential.Id,c.Id);
        Assert.Equal(CredentialSecretStorage.SqliteDpapi,c.SecretStorage);
        Assert.Equal("SELECTED synthetic secret",await context.ReadSecretAsync(c));
        await Assert.ThrowsAsync<CredentialUserContextException>(()=>target.Vault.ReadSecretAsync(c));
        Assert.Single((await target.Repository.GetAllConnectionsAsync()).Select(e=>e.CredentialId).Distinct());
        var ignore=await importer.ImportEncryptedPackageAsync(path,"Synthetic transport",DuplicateImportHandling.Ignore);
        Assert.Equal(0,ignore.ConnectionsImported);Assert.Single(await target.Repository.GetAllCredentialsAsync());
        await importer.ImportEncryptedPackageAsync(path,"Synthetic transport",DuplicateImportHandling.Update);
        Assert.Equal(2,(await target.Repository.GetAllConnectionsAsync()).Count);
        var duplicate=await importer.ImportEncryptedPackageAsync(path,"Synthetic transport",DuplicateImportHandling.CreateNew);
        Assert.Equal(2,duplicate.ConnectionsImported);
        Assert.Equal(4,(await target.Repository.GetAllConnectionsAsync()).Count);
    }

    [Fact]
    public async Task UnavailableSelectedSecretBlocksBeforeOutput_UnselectedAndUnlinkedSecretsAreNeverRead()
    {
        using var f=new Fixture();var seed=await SeedTransfer(f);
        var broken=(await f.Repository.GetCredentialByIdAsync(seed.Excluded.CredentialId!))!;
        broken.ProtectedSecretBase64="not-decryptable";await f.Repository.SaveCredentialAsync(broken);
        var service=new ImportExportService(f.Repository,f.Vault,f.Logger);
        var file=Path.Combine(f.Paths.RootDirectory,"result.ctienc");
        await service.ExportTransferPackageAsync(file,new[]{seed.A.Id},true,"Transfer pass","Transfer pass");
        var original=await File.ReadAllBytesAsync(file);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExportTransferPackageAsync(file,new[]{seed.Excluded.Id},
            true,"Transfer pass","Transfer pass"));
        Assert.Equal(original,await File.ReadAllBytesAsync(file));
        var missing=Path.Combine(f.Paths.RootDirectory,"not-created.ctienc");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExportTransferPackageAsync(missing,new[]{seed.Excluded.Id},
            true,"Transfer pass","Transfer pass"));
        Assert.False(File.Exists(missing));
        Assert.Equal("not-decryptable",(await f.Repository.GetCredentialByIdAsync(broken.Id))!.ProtectedSecretBase64);
    }

    private sealed class ProgressAction(Action<ImportProgress> action) : IProgress<ImportProgress>
    {public void Report(ImportProgress value)=>action(value);}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferImportRollbackAndCancellationLeaveNoPartialRows(bool cancel)
    {
        using var source=new Fixture();using var target=new Fixture();
        var seed=await SeedTransfer(source);
        var path=Path.Combine(source.Paths.RootDirectory,"rollback.ctienc");
        await new ImportExportService(source.Repository,source.Vault,source.Logger)
            .ExportTransferPackageAsync(path,new[]{seed.A.Id,seed.B.Id},true,"Rollback transport","Rollback transport");
        var importer=new ImportExportService(target.Repository,target.Vault,target.Logger);
        var preview=await importer.ReadEncryptedPreviewAsync(path,"Rollback transport");
        if(!cancel)target.Sql("CREATE TRIGGER fail_transfer BEFORE INSERT ON Connections WHEN NEW.Name='Selected B' BEGIN SELECT RAISE(ABORT,'injected'); END;");
        using var cancellation=new CancellationTokenSource();
        if(cancel)cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(()=>importer.CommitImportAsync(preview,DuplicateImportHandling.Ignore,token:cancellation.Token));
        Assert.Empty(await target.Repository.GetAllConnectionsAsync());Assert.Empty(await target.Repository.GetAllCredentialsAsync());
    }

    [Fact]
    public async Task FilePasswordConfirmationTrashSelectionAndInvalidContentAreRejected()
    {
        using var f=new Fixture();var seed=await SeedTransfer(f);
        var service=new ImportExportService(f.Repository,f.Vault,f.Logger);
        var path=Path.Combine(f.Paths.RootDirectory,"invalid.ctienc");
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ExportTransferPackageAsync(path,new[]{seed.A.Id},true,"Long enough","different"));
        var trash=(await f.Repository.GetAllConnectionsAsync(includeDeleted:true)).Single(e=>e.IsDeleted);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExportTransferPackageAsync(path,new[]{trash.Id},false));
        var invalid=new PortableEncryptedBundle{FormatVersion="ConectaTI-Portable-2.0",IncludesPasswords=false,
            Connections=new(){seed.A},Credentials=new()};
        var bytes=JsonSerializer.SerializeToUtf8Bytes(invalid);
        await File.WriteAllBytesAsync(path,"CTIMETA2"u8.ToArray().Concat(bytes).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ReadEncryptedPreviewAsync(path,""));
    }

    [Fact]
    public void SelectionSurvivesSearchSpecificFiltersReviewAndReload()
    {
        var entries=new[]{
            new ConnectionEntry{Id="one",Name="Alpha",ClientName="Client One",GroupName="Linux",Environment=EnvironmentType.Production},
            new ConnectionEntry{Id="two",Name="Beta",ClientName="Client Two",GroupName="Windows",Environment=EnvironmentType.Testing},
            new ConnectionEntry{Id="trash",Name="Discarded",IsDeleted=true}};
        var selection=new ExportSelectionModel();selection.Reload(entries);Assert.Equal(2,selection.SelectedIds.Length);
        selection.Clear();selection.Client="One";selection.SelectFiltered();
        Assert.Equal("one",Assert.Single(selection.SelectedIds));
        selection.Client="Two";Assert.Equal("one",Assert.Single(selection.SelectedIds));
        selection.SelectFiltered();Assert.Equal(2,selection.SelectedIds.Length);
        selection.Client="";selection.Group="Linux";Assert.Equal("one",Assert.Single(selection.Filtered).Entry.Id);
        selection.Group="";selection.Name="Beta";Assert.Equal("two",Assert.Single(selection.Filtered).Entry.Id);
        selection.Name="";selection.Query="Alpha";Assert.Single(selection.Filtered);
        selection.Query="";selection.Environment=entries[1].EnvironmentDisplay;Assert.Single(selection.Filtered);
        selection.Environment="";selection.Items.Single(i=>i.Entry.Id=="two").IsSelected=false;
        selection.ReviewOnly=true;Assert.Equal("one",Assert.Single(selection.Filtered).Entry.Id);
        selection.Reload(entries);Assert.Equal("one",Assert.Single(selection.SelectedIds));Assert.False(selection.ExportAll);
        selection.ExportAll=true;Assert.Equal(2,selection.SelectedIds.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderMasterPlusDpapiMigratesOnce_WithWindowsOriginalPreserved(bool inWindows)
    {
        using var f=new Fixture();
        const string password="Synthetic older master";
        var salt=RandomNumberGenerator.GetBytes(32);
        var key=Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),salt,210_000,HashAlgorithmName.SHA256,32);
        byte[] EncryptOld(byte[] plain)
        {
            var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[plain.Length];
            using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher,tag);
            return nonce.Concat(tag).Concat(cipher).ToArray();
        }
        f.Settings.Current.HasMasterPassword=true;
        f.Settings.Current.MasterPasswordSaltBase64=Convert.ToBase64String(salt);
        f.Settings.Current.MasterPasswordVerifierBase64=Convert.ToBase64String(EncryptOld(Encoding.UTF8.GetBytes("ConectaTI::MasterKeyVerifier::OK::v1")));
        var old="MKGCM1:"+Convert.ToBase64String(ProtectedData.Protect(EncryptOld(Encoding.UTF8.GetBytes("older protected secret")),
            Encoding.UTF8.GetBytes("ConectaTI::DPAPI::CurrentUser::Entropy::v1"),DataProtectionScope.CurrentUser));
        var c=new SavedCredential{Name="Old master fixture",SecretStorage=inWindows?CredentialSecretStorage.LegacyWindows:CredentialSecretStorage.SqliteDpapi,
            ProtectedSecretBase64=inWindows?"":old};
        await f.Repository.SaveCredentialAsync(c);
        var vault=new ForbiddenWindowsVault(f.Settings,f.Repository,f.Logger);
        if(inWindows)vault.Legacy[c.Id]=old;
        Assert.True(await vault.HasPreviousVaultCredentialsAsync());
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>vault.MigratePreviousVaultAsync("incorrect"));
        Assert.Equal(c.ProtectedSecretBase64,(await f.Repository.GetCredentialByIdAsync(c.Id))!.ProtectedSecretBase64);
        await vault.MigratePreviousVaultAsync(password);
        Assert.False(await vault.HasPreviousVaultCredentialsAsync());
        Assert.Equal("older protected secret",await vault.ReadSecretAsync(c));
        vault.Lock();Assert.Equal("older protected secret",await vault.ReadSecretAsync(c));
        Assert.Equal(0,vault.Writes);Assert.Equal(0,vault.Deletes);
        if(inWindows)Assert.Equal(old,vault.Legacy[c.Id]);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void PreviouslyExportedVersionOneTransportRemainsReadable()
    {
        var salt=RandomNumberGenerator.GetBytes(32);var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];
        var plain=Encoding.UTF8.GetBytes("synthetic old transport payload");var cipher=new byte[plain.Length];
        var key=Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("Old transport"),salt,210_000,HashAlgorithmName.SHA256,32);
        using(var aes=new AesGcm(key,16))aes.Encrypt(nonce,plain,cipher,tag);
        var file="CTIENC01"u8.ToArray().Concat(salt).Concat(nonce).Concat(tag).Concat(cipher).ToArray();
        Assert.Equal(plain,CredentialVaultService.DecryptPortablePayload(file,"Old transport"));
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task ForeignContextCannotRevealOrCopy_AndRemoteClientCanAskForManualCredentials()
    {
        using var f=new Fixture();
        var c=new SavedCredential{Name="Protected source"};
        await f.Vault.SaveApplicationCredentialAsync(c,"SOURCE unavailable secret");
        var e=new ConnectionEntry{Name="Manual authentication",Host="example.test",Protocol=ConnectionProtocol.Ssh,
            SshClient=SshClientPreference.OpenSsh,CredentialId=c.Id};
        await f.Repository.SaveConnectionAsync(e);
        f.Settings.Current.OpenSshExecutablePath=Path.Combine(Environment.SystemDirectory,"OpenSSH","ssh.exe");
        var other=new OtherContext(f.Settings,f.Repository,f.Logger);
        string clipboard="untouched";var starts=0;
        var clip=new ClipboardService(f.Logger,value=>{clipboard=value;return true;},()=>clipboard,()=>{});
        var launcher=new ConectaTI.App.Services.Launchers.ConnectionLauncherService(f.Paths,f.Settings,f.Repository,other,clip,f.Logger,psi=>
        {
            starts++;Assert.DoesNotContain(psi.ArgumentList,arg=>arg.Contains("SOURCE unavailable secret"));return Environment.ProcessId;
        });
        var vm=new ConectaTI.App.ViewModels.MainViewModel(f.Paths,f.Logger,f.Settings,f.Repository,other,clip,launcher,
            new ImportExportService(f.Repository,other,f.Logger),new ConectaTI.App.Services.Diagnostics.NetworkDiagnosticService());
        await vm.InitializeAsync();
        Assert.True(vm.HasSavedPassword);Assert.True(vm.PasswordRecoveryFailed);
        Assert.Contains("outro usuário",vm.SavedPasswordBadgeText);
        await vm.TogglePasswordVisibilityAsync();Assert.False(vm.IsPasswordRevealed);Assert.Empty(vm.RevealedPasswordText);
        await vm.CopyPasswordAsync();Assert.Equal("untouched",clipboard);
        var result=await launcher.LaunchAsync(e);
        Assert.True(result.Started);Assert.Equal(1,starts);
        Assert.Contains("outro usuário",result.SecurityNotice);Assert.Contains("manualmente",result.SecurityNotice);
        Assert.Equal(c.ProtectedSecretBase64,(await f.Repository.GetCredentialByIdAsync(c.Id))!.ProtectedSecretBase64);
        vm.ClearSessionDrafts();
    }
}
