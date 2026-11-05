using System.IO;
using System.Windows.Controls;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;
using Microsoft.Data.Sqlite;
namespace ConectaTI.Tests;

public sealed class CredentialSelectionRaceTests : IDisposable
{
    private readonly AppPaths paths=new(Path.Combine(Path.GetTempPath(),"ConectaTI_Race_"+Guid.NewGuid().ToString("N")));
    private readonly AppLogger logger;
    private readonly SqliteRepository repo;
    private readonly SettingsService settings;
    private readonly DelayedVault vault;
    private string? copied;
    public CredentialSelectionRaceTests()
    {
        ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        logger=new(paths);settings=new(paths,logger);settings.Load();
        repo=new(paths,logger);repo.InitializeAndMigrate();vault=new(settings,repo,logger);
    }
    private async Task<(MainViewModel Vm,ConnectionEntry A,ConnectionEntry B)> Seed()
    {
        var cred=new SavedCredential{Name="Synthetic A",ProtectedSecretBase64="synthetic-test-envelope"};
        await repo.SaveCredentialAsync(cred);
        var a=new ConnectionEntry{Name="Alpha",Host="a.example.test",CredentialId=cred.Id};
        var b=new ConnectionEntry{Name="Beta",Host="b.example.test"};
        await repo.SaveConnectionsBatchAsync([a,b]);
        var clip=new ClipboardService(logger,text=>{copied=text;return true;},()=>copied,()=>copied=null);
        var vm=new MainViewModel(paths,logger,settings,repo,vault,clip,new(paths,settings,repo,vault,clip,logger),new(repo,vault,logger),new NetworkDiagnosticService());
        await vm.InitializeAsync(seedDemoIfEmpty:false);vm.SelectedConnection=a;
        return(vm,a,b);
    }
    [Theory]
    [InlineData("copy",false)] [InlineData("copy",true)]
    [InlineData("reveal",false)] [InlineData("reveal",true)]
    [InlineData("all",false)] [InlineData("all",true)]
    public Task LateSecretOrFailureNeverAffectsNextSelection(string operation,bool failure) => StaTest.RunAsync(async ()=>
    {
        var (vm,a,b)=await Seed();
        var pending=operation=="copy"?vm.CopyPasswordAsync():operation=="all"?vm.CopyAllDataAsync():vm.TogglePasswordVisibilityAsync();
        await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedConnection=b;vm.StatusMessage="CURRENT B";
        if(failure)vault.ReadResult.SetException(new InvalidOperationException("Synthetic read failure"));
        else vault.ReadResult.SetResult("SYNTHETIC_A_SECRET");
        await pending;
        Assert.Null(copied); Assert.False(vm.IsPasswordRevealed);Assert.Empty(vm.RevealedPasswordText);
        Assert.Equal("CURRENT B",vm.StatusMessage);Assert.Equal(b.Id,vm.EditingEntry.Id);
        vm.SelectedConnection=a; Assert.False(vm.IsPasswordRevealed);
    });
    [Theory]
    [InlineData("edit")] [InlineData("close")] [InlineData("return")]
    public Task NewerRevisionClosingAndReturningToSameIdInvalidatePendingRead(string action)=>StaTest.RunAsync(async ()=>
    {
        var(vm,a,b)=await Seed();var pending=vm.CopyPasswordAsync();
        await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(action=="edit")vm.EditingEntry.Notes="newer edit";
        else if(action=="close")vm.ClearSessionDrafts();
        else {vm.SelectedConnection=b;vm.SelectedConnection=a;}
        vm.StatusMessage="CURRENT";
        vault.ReadResult.SetResult("SYNTHETIC_A_SECRET");await pending;
        Assert.Null(copied);Assert.Equal("CURRENT",vm.StatusMessage);
    });
    [Fact]
    public Task ClipboardRetryChecksSelectionAgain()=>StaTest.RunAsync(async ()=>
    {
        var current=true; var attempts=0;
        var clipboard=new ClipboardService(logger,_=>{attempts++;current=false;return false;},()=>null,()=>{});
        Assert.False(await clipboard.CopyTextWithRetryAsync("SYNTHETIC",true,30,()=>current));
        Assert.Equal(1,attempts);
    });
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RealCredentialDialogDiscardsLateRevealAndFailure(bool failure)=>StaTest.RunAsync(async ()=>
    {
        var a=new SavedCredential{Name="Alpha",ProtectedSecretBase64="synthetic"};
        var b=new SavedCredential{Name="Beta",ProtectedSecretBase64="synthetic"};
        await repo.SaveCredentialAsync(a);await repo.SaveCredentialAsync(b);
        var dialog=new CredentialsDialog(repo,vault);
        try
        {
            dialog.Show();
            var list=(ListBox)dialog.FindName("CredentialsListBox");
            for(var n=0;list.Items.Count<2&&n<100;n++)await Task.Delay(10);
            Assert.Equal(2,list.Items.Count);
            list.SelectedItem=list.Items.Cast<SavedCredential>().Single(c=>c.Id==a.Id);
            var pending=dialog.RevealSelectedPasswordAsync();
            await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list.SelectedItem=list.Items.Cast<SavedCredential>().Single(c=>c.Id==b.Id);
            ((TextBlock)dialog.FindName("StatusText")).Text="CURRENT B";
            if(failure)vault.ReadResult.SetException(new InvalidOperationException("Synthetic failure"));
            else vault.ReadResult.SetResult("SYNTHETIC_A_SECRET");
            await pending;
            Assert.DoesNotContain("SYNTHETIC_A_SECRET",((TextBlock)dialog.FindName("RevealedSecretBox")).Text);
            Assert.Equal("Beta",((TextBox)dialog.FindName("CredNameBox")).Text);
            Assert.Equal("CURRENT B",((TextBlock)dialog.FindName("StatusText")).Text);
        }
        finally {dialog.Close();}
    });
    [Fact]
    public Task RealCredentialDialogDoesNotReselectAfterLateSave()=>StaTest.RunAsync(async ()=>
    {
        var a=new SavedCredential{Name="Alpha"};var b=new SavedCredential{Name="Beta"};
        await repo.SaveCredentialAsync(a);await repo.SaveCredentialAsync(b);
        var dialog=new CredentialsDialog(repo,vault);
        try
        {
            dialog.Show();var list=(ListBox)dialog.FindName("CredentialsListBox");
            for(var n=0;list.Items.Count<2&&n<100;n++)await Task.Delay(10);
            list.SelectedItem=list.Items.Cast<SavedCredential>().Single(c=>c.Id==a.Id);
            ((TextBox)dialog.FindName("CredNotesBox")).Text="submitted A";
            var pending=dialog.SaveSelectedCredentialAsync();
            await vault.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list.SelectedItem=list.Items.Cast<SavedCredential>().Single(c=>c.Id==b.Id);
            ((TextBox)dialog.FindName("CredNotesBox")).Text="newer B";
            vault.SaveRelease.SetResult();await pending;
            Assert.Equal(b.Id,((SavedCredential)list.SelectedItem).Id);
            Assert.Equal("newer B",((TextBox)dialog.FindName("CredNotesBox")).Text);
            Assert.Equal("submitted A",vault.SavedSnapshot!.Notes);
            Assert.Empty((await repo.GetCredentialByIdAsync(a.Id))!.Notes); // substitute never commits
        }
        finally {dialog.Close();}
    });
    private sealed class DelayedVault(SettingsService settings,SqliteRepository repo,AppLogger logger)
        : CredentialVaultService(settings,repo,logger,useWindowsVault:false)
    {
        public TaskCompletionSource Entered {get;}=new();
        public TaskCompletionSource<string> ReadResult {get;}=new();
        public TaskCompletionSource SaveEntered {get;}=new();
        public TaskCompletionSource SaveRelease {get;}=new();
        public SavedCredential? SavedSnapshot {get;private set;}
        public override async Task<string> ReadSecretAsync(SavedCredential credential)
        {Entered.TrySetResult();return await ReadResult.Task;}
        public override async Task SaveApplicationCredentialAsync(SavedCredential credential,string? replacementPassword=null)
        {SavedSnapshot=credential;SaveEntered.TrySetResult();await SaveRelease.Task;}
    }
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(paths.RootDirectory,true);}catch{}}
}
