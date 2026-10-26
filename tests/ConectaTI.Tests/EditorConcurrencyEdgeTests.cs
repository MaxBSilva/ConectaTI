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

public sealed class EditorConcurrencyEdgeTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Edge_" + Guid.NewGuid().ToString("N")));
    private readonly GateRepository _repo;
    private readonly MainViewModel _vm;

    public EditorConcurrencyEdgeTests()
    {
        ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        settings.Load();
        _repo = new GateRepository(_paths, logger);
        _repo.InitializeAndMigrate();
        var vault = new CredentialVaultService(settings, _repo, logger, useWindowsVault: false); VaultTestSupport.Initialize(vault);
        var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });
        _vm = new MainViewModel(_paths, logger, settings, _repo, vault, clipboard,
            new ConnectionLauncherService(_paths, settings, _repo, vault, clipboard, logger),
            new ImportExportService(_repo, vault, logger), new NetworkDiagnosticService());
    }

    private async Task<(ConnectionEntry A, ConnectionEntry B)> SeedAsync()
    {
        var a = new ConnectionEntry { Name = "Alpha", Host = "alpha.example.test", ClientName = "Fictício" };
        var b = new ConnectionEntry { Name = "Beta", Host = "beta.example.test", ClientName = "Fictício" };
        await _repo.SaveConnectionsBatchAsync([a, b]);
        await _vm.InitializeAsync();
        return (a, b);
    }

    [Fact]
    public async Task ErasingNewPasswordDuringCommitKeepsRemovalPending()
    {
        var (a, _) = await SeedAsync();
        _vm.UpdatePasswordFromUser("submitted-test-secret");
        _repo.PauseSave = true;
        var save = _vm.SaveConnectionAsync();
        await _repo.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _vm.UpdatePasswordFromUser("");
        _repo.SaveRelease.TrySetResult();
        Assert.True(await save);
        Assert.True(_vm.HasPendingChanges);
        Assert.Equal("Remoção pendente — salvar", _vm.SavedPasswordBadgeText);
        Assert.True(await _vm.SaveConnectionAsync());
        Assert.Null((await _repo.GetConnectionByIdAsync(a.Id))!.CredentialId);
    }

    [Fact]
    public async Task OldReloadCannotRestoreSelectionAfterMoreRecentNavigation()
    {
        var (a, b) = await SeedAsync();
        _repo.PauseRead = true;
        var reload = _vm.ReloadAllDataAsync(a.Id);
        await _repo.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _vm.RequestNavigateToConnectionAsync(_vm.FilteredConnections.Single(c => c.Id == b.Id));
        _vm.EditingEntry.Notes = "newer B";
        _repo.ReadRelease.TrySetResult();
        await reload;
        Assert.Equal(b.Id, _vm.SelectedConnection!.Id);
        Assert.Equal("newer B", _vm.EditingEntry.Notes);
    }

    [Fact]
    public async Task FavoriteChangesAreDraftsUntilExplicitSave()
    {
        var (a, _) = await SeedAsync();
        await _vm.ToggleFavoriteAsync(_vm.SelectedConnection);
        Assert.True(_vm.EditingEntry.IsFavorite);
        Assert.True(_vm.HasPendingChanges);
        Assert.False((await _repo.GetConnectionByIdAsync(a.Id))!.IsFavorite);
        Assert.True(await _vm.SaveConnectionAsync());
        Assert.True((await _repo.GetConnectionByIdAsync(a.Id))!.IsFavorite);
    }

    [Fact]
    public async Task DiscardCannotRaceAnAlreadySubmittedSave()
    {
        await SeedAsync();
        _vm.EditingEntry.Notes = "submitted";
        _repo.PauseSave = true;
        var save = _vm.SaveConnectionAsync();
        await _repo.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_vm.CanDiscardChanges);
        _vm.CancelEdit();
        _vm.EditingEntry.Notes = "later";
        _repo.SaveRelease.TrySetResult();
        Assert.True(await save);
        Assert.Equal("later", _vm.EditingEntry.Notes);
        Assert.True(_vm.HasPendingChanges);
        _vm.CancelEdit();
        Assert.Equal("submitted", _vm.EditingEntry.Notes);
        Assert.False(_vm.HasPendingChanges);
    }

    private sealed class GateRepository(AppPaths paths, AppLogger logger) : SqliteRepository(paths, logger)
    {
        public bool PauseRead { get; set; }
        public bool PauseSave { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<List<ConnectionEntry>> GetAllConnectionsAsync(bool includeDeleted = false)
        {
            var results = await base.GetAllConnectionsAsync(includeDeleted);
            if (PauseRead)
            {
                ReadEntered.TrySetResult();
                await ReadRelease.Task;
                PauseRead = false;
            }
            return results;
        }
        public override async Task SaveConnectionWithCredentialAsync(ConnectionEntry entry, SavedCredential? credential)
        {
            if (PauseSave)
            {
                SaveEntered.TrySetResult();
                await SaveRelease.Task;
                PauseSave = false;
            }
            await base.SaveConnectionWithCredentialAsync(entry, credential);
        }
    }

    public void Dispose()
    {
        _vm.ClearSessionDrafts();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch (IOException) { }
    }
}
