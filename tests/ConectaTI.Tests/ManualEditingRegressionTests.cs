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

public sealed class ManualEditingRegressionTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Regression_" + Guid.NewGuid().ToString("N")));
    private readonly AppLogger _logger;
    private readonly SettingsService _settings;
    private readonly ControlledRepository _repository;
    private readonly ControlledVault _vault;

    public ManualEditingRegressionTests()
    {
        _logger = new AppLogger(_paths);
        _settings = new SettingsService(_paths, _logger);
        _settings.Load();
        _repository = new ControlledRepository(_paths, _logger);
        _repository.InitializeAndMigrate();
        _vault = new ControlledVault(_settings, _repository, _logger);
    }

    private MainViewModel CreateVm()
    {
        var clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
        return new MainViewModel(_paths, _logger, _settings, _repository, _vault, clipboard,
            new ConnectionLauncherService(_paths, _settings, _repository, _vault, clipboard, _logger),
            new ImportExportService(_repository, _vault, _logger), new NetworkDiagnosticService());
    }

    private async Task<(MainViewModel Vm, ConnectionEntry A, ConnectionEntry B)> PairAsync(bool password = false)
    {
        var a = new ConnectionEntry { Name = "Alpha", ClientName = "Cliente", Host = "10.0.0.1", IsFavorite = true, LastOpenedAtUtc = DateTime.UtcNow };
        var b = new ConnectionEntry { Name = "Beta", ClientName = "Outro", Host = "10.0.0.2" };
        if (password)
        {
            var credential = new SavedCredential { Name = "Shared" };
            credential.ProtectedSecretBase64 = _vault.ProtectSecret(credential.Id, "", "original", false);
            await _repository.SaveCredentialAsync(credential);
            a.CredentialId = b.CredentialId = credential.Id;
        }
        await _repository.SaveConnectionsBatchAsync([a, b]);
        var vm = CreateVm();
        await vm.InitializeAsync();
        return (vm, a, b);
    }

    [Fact]
    public async Task Navigation_KeepsSeparateDrafts_AndNeverCallsSaveConfirmation()
    {
        var (vm, a, b) = await PairAsync();
        vm.ConfirmUnsavedChangesCallback = _ => throw new Exception("Unexpected prompt");
        vm.EditingEntry.Name = "A draft";
        vm.InlinePasswordInput = "new secret";
        await vm.RequestNavigateToConnectionAsync(b);
        Assert.Equal(b.Id, vm.EditingEntry.Id);
        Assert.Equal("Beta", vm.EditingEntry.Name);
        Assert.Empty(vm.InlinePasswordInput);
        vm.EditingEntry.Notes = "B draft";
        vm.SelectedConnection = a;
        Assert.Equal("A draft", vm.EditingEntry.Name);
        Assert.Equal("new secret", vm.InlinePasswordInput);
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal("Beta", (await _repository.GetConnectionByIdAsync(b.Id))!.Name);
        Assert.Empty((await _repository.GetConnectionByIdAsync(b.Id))!.Notes);
        vm.SelectedConnection = vm.FilteredConnections.Single(c => c.Id == b.Id);
        Assert.Equal("B draft", vm.EditingEntry.Notes);
        Assert.Empty(vm.InlinePasswordInput);
    }

    [Fact]
    public async Task NewDrafts_HaveStableSeparateIdentities_RevertDiscardAndSaveWithoutDuplicates()
    {
        var (vm, a, _) = await PairAsync();
        vm.NewConnection();
        Assert.False(vm.HasPendingChanges);
        Assert.False(vm.CanSave);
        var first = vm.EditingEntry;
        first.Name = "New one";
        vm.InlineHostAndPort = "10.1.1.1";
        vm.NewConnection();
        var second = vm.EditingEntry;
        Assert.NotEqual(first.Id, second.Id);
        vm.EditingEntry.Name = "Second";
        vm.SelectedConnection = a;
        vm.SelectedNewDraft = first;
        Assert.Equal("New one", vm.EditingEntry.Name);
        Assert.True(await vm.SaveConnectionAsync());
        Assert.False(await vm.SaveConnectionAsync());
        Assert.Single(await _repository.GetAllConnectionsAsync(), c => c.Id == first.Id);
        Assert.Single(vm.NewDrafts);
        vm.SelectedNewDraft = second;
        vm.CancelEdit();
        Assert.Empty(vm.NewDrafts);
        Assert.Null(await _repository.GetConnectionByIdAsync(second.Id));
        vm.EditingEntry.Name += " changed";
        Assert.True(vm.CanSave);
        vm.CancelEdit();
        var original = vm.EditingEntry.Name;
        vm.EditingEntry.Name += "x";
        vm.EditingEntry.Name = original;
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public async Task SaveCompletion_DoesNotReplaceB_OrLoseLaterAEdits()
    {
        var (vm, a, b) = await PairAsync();
        vm.EditingEntry.Name = "Saved A";
        _repository.PauseSave();
        var save = vm.SaveConnectionAsync();
        await _repository.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsSaving);
        Assert.Equal("Salvando...", vm.SaveButtonText);
        Assert.False(await vm.SaveConnectionAsync());
        vm.EditingEntry.Name = "Newer A";
        vm.SelectedConnection = b;
        vm.EditingEntry.Notes = "Newer B";
        _repository.ContinueSave();
        Assert.True(await save);
        Assert.Equal(b.Id, vm.EditingEntry.Id);
        Assert.Equal("Newer B", vm.EditingEntry.Notes);
        Assert.True(vm.HasPendingChanges);
        vm.SelectedConnection = vm.FilteredConnections.Single(c => c.Id == a.Id);
        Assert.Equal("Newer A", vm.EditingEntry.Name);
        Assert.True(vm.HasPendingChanges);
        Assert.Equal("Saved A", (await _repository.GetConnectionByIdAsync(a.Id))!.Name);
        Assert.True(await vm.SaveConnectionAsync());
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public async Task SaveCompletion_ClearsOnlyThePasswordRevisionActuallyCommitted()
    {
        var (vm, a, _) = await PairAsync(password: true);
        vm.InlinePasswordInput = "first";
        _repository.PauseSave();
        var save = vm.SaveConnectionAsync();
        await _repository.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.InlinePasswordInput = "second";
        _repository.ContinueSave();
        Assert.True(await save);
        Assert.Equal("second", vm.InlinePasswordInput);
        Assert.Equal("Senha alterada — salvar", vm.SavedPasswordBadgeText);
        Assert.True(vm.CanSave);
        var saved = await _repository.GetConnectionByIdAsync(a.Id);
        Assert.Equal("first", _vault.UnprotectSecret((await _repository.GetCredentialByIdAsync(saved!.CredentialId!))!));
        Assert.True(await vm.SaveConnectionAsync());
        saved = await _repository.GetConnectionByIdAsync(a.Id);
        Assert.Equal("second", _vault.UnprotectSecret((await _repository.GetCredentialByIdAsync(saved!.CredentialId!))!));
    }

    [Fact]
    public async Task Password_KeepRevealEraseDiscardReplaceAndSharedRemoval()
    {
        var (vm, a, b) = await PairAsync(password: true);
        var sharedId = a.CredentialId!;
        vm.EditingEntry.Notes = "name only";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal(sharedId, vm.EditingEntry.CredentialId);
        await vm.TogglePasswordVisibilityAsync();
        Assert.False(vm.HasPendingChanges);
        Assert.Equal("original", vm.RevealedPasswordText);
        vm.UpdatePasswordFromUser("");
        Assert.True(vm.IsPasswordMarkedForRemoval);
        Assert.Equal("Remoção pendente — salvar", vm.SavedPasswordBadgeText);
        vm.CancelEdit();
        Assert.False(vm.HasPendingChanges);
        Assert.Equal("Senha salva", vm.SavedPasswordBadgeText);
        vm.InlinePasswordInput = "replacement";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.NotEqual(sharedId, vm.EditingEntry.CredentialId);
        Assert.Equal(sharedId, (await _repository.GetConnectionByIdAsync(b.Id))!.CredentialId);
        Assert.Equal("original", _vault.UnprotectSecret((await _repository.GetCredentialByIdAsync(sharedId))!));
        vm.MarkPasswordForRemoval();
        Assert.True(await vm.SaveConnectionAsync());
        var reopened = CreateVm();
        await reopened.InitializeAsync();
        reopened.SelectedConnection = reopened.FilteredConnections.Single(c => c.Id == a.Id);
        Assert.Equal("Sem senha salva", reopened.SavedPasswordBadgeText);
        Assert.Null((await _repository.GetConnectionByIdAsync(a.Id))!.CredentialId);
        Assert.NotNull(await _repository.GetCredentialByIdAsync(sharedId));
    }

    [Fact]
    public async Task Replacement_DoesNotDeleteCredentialSharedWithTrash()
    {
        var (vm, a, b) = await PairAsync(password: true);
        await _repository.SoftDeleteConnectionAsync(b.Id);
        await vm.ReloadAllDataAsync(a.Id);
        vm.InlinePasswordInput = "replacement";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal(a.CredentialId, (await _repository.GetConnectionByIdAsync(b.Id))!.CredentialId);
        Assert.NotNull(await _repository.GetCredentialByIdAsync(a.CredentialId!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailure_KeepsDraftAndOldSecret_AndAllowsRetry(bool vaultFailure)
    {
        var (vm, a, _) = await PairAsync(password: true);
        vm.InlinePasswordInput = "replacement";
        vm.EditingEntry.Name = "Edited";
        if (vaultFailure) _vault.FailWrite = true;
        else _repository.FailSave = true;
        Assert.False(await vm.SaveConnectionAsync());
        Assert.Equal("replacement", vm.InlinePasswordInput);
        Assert.Equal("Edited", vm.EditingEntry.Name);
        Assert.True(vm.CanSave);
        Assert.Equal(a.CredentialId, (await _repository.GetConnectionByIdAsync(a.Id))!.CredentialId);
        Assert.Equal("original", _vault.UnprotectSecret((await _repository.GetCredentialByIdAsync(a.CredentialId!))!));
        _vault.FailWrite = false;
        _repository.FailSave = false;
        Assert.True(await vm.SaveConnectionAsync());
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public async Task SqliteTransaction_RollsBackNewCredentialWhenConnectionWriteFails()
    {
        var (vm, a, _) = await PairAsync(password: true);
        using (var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_connection BEFORE UPDATE ON Connections BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            command.ExecuteNonQuery();
        }
        var count = (await _repository.GetAllCredentialsAsync()).Count;
        vm.InlinePasswordInput = "replacement";
        Assert.False(await vm.SaveConnectionAsync());
        Assert.Equal(count, (await _repository.GetAllCredentialsAsync()).Count);
        Assert.Equal(a.CredentialId, (await _repository.GetConnectionByIdAsync(a.Id))!.CredentialId);
        Assert.True(vm.HasPendingChanges);
        Assert.Empty(await _repository.GetPendingVaultCleanupAsync());
    }

    [Fact]
    public async Task VaultCleanupFailure_IsDurableAndRecoveredWithoutUnlinkingOtherAccesses()
    {
        var (vm, a, _) = await PairAsync(password: true);
        // Make the original secret exclusive to A, then remove it.
        var b = vm.FilteredConnections.Single(c => c.Id != a.Id).Clone();
        b.CredentialId = null;
        await _repository.SaveConnectionAsync(b);
        _vault.FailDelete = true;
        vm.MarkPasswordForRemoval();
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Null((await _repository.GetConnectionByIdAsync(a.Id))!.CredentialId);
        Assert.Contains(a.CredentialId!, await _repository.GetPendingVaultCleanupAsync());
        Assert.Contains("Limpeza", vm.StatusMessage);
        _vault.FailDelete = false;
        var recovery = new ConnectionPersistenceService(_repository, _vault, _logger);
        Assert.True(await recovery.RecoverVaultCleanupAsync());
        Assert.Empty(await _repository.GetPendingVaultCleanupAsync());
        Assert.Contains(a.CredentialId!, _vault.Deleted);
    }

    [Fact]
    public async Task ReloadFailure_PreservesListAndSelection_AndFiltersSurviveSave()
    {
        var (vm, a, _) = await PairAsync();
        vm.SearchText = "Al";
        vm.SelectedClientFilter = "Cliente";
        vm.SelectedProtocolFilter = "Área de Trabalho Remota (MSTSC)";
        vm.SelectedEnvironmentFilter = "Produção";
        vm.SelectedSortOption = ConnectionSortOption.ClientAsc;
        await vm.SetViewModeAsync(ConnectionViewMode.Favorites);
        vm.EditingEntry.Notes = "edit";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Single(vm.FilteredConnections);
        Assert.Equal(a.Id, vm.SelectedConnection!.Id);
        Assert.Equal(a.Id, vm.EditingEntry.Id);
        Assert.Equal("Al", vm.SearchText);
        Assert.Equal("Cliente", vm.SelectedClientFilter);
        Assert.Equal(ConnectionViewMode.Favorites, vm.CurrentViewMode);
        Assert.Equal(ConnectionSortOption.ClientAsc, vm.SelectedSortOption);
        var list = vm.FilteredConnections;
        _repository.FailReload = true;
        await vm.ReloadAllDataAsync();
        Assert.Same(list, vm.FilteredConnections);
        Assert.Equal(a.Id, vm.EditingEntry.Id);
        Assert.Contains("mantidos", vm.StatusMessage);
    }

    [Fact]
    public async Task Reload_RefreshesCleanFormForTheSameSelectedIdentity()
    {
        var (vm, a, _) = await PairAsync();
        a.Name = "Updated outside the editor";
        await _repository.SaveConnectionAsync(a);
        await vm.ReloadAllDataAsync(a.Id);
        Assert.Equal(a.Id, vm.EditingEntry.Id);
        Assert.Equal(a.Name, vm.EditingEntry.Name);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public async Task WindowsVault_RoundTripUsesOnlyAnIsolatedSyntheticCredential()
    {
        var vault = new CredentialVaultService(_settings, _repository, _logger, useWindowsVault: true);
        var credential = new SavedCredential { Name = "ConectaTI regression", Username = "fictional" };
        try
        {
            credential.ProtectedSecretBase64 = vault.ProtectSecret(credential.Id, credential.Username, "test-only");
            credential.ProtectedSecretBase64 = string.Empty; // Force the native Windows vault read.
            Assert.Equal("test-only", vault.UnprotectSecret(credential));
            vault.RemoveFromWindowsVault(credential.Id);
            Assert.Empty(vault.UnprotectSecret(credential));
        }
        finally { vault.RemoveFromWindowsVault(credential.Id); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ClosingSession_DiscardsOnlyDrafts_AndPersistedDataSurvives()
    {
        var (vm, a, b) = await PairAsync();
        vm.EditingEntry.Name = "Persisted";
        Assert.True(await vm.SaveConnectionAsync());
        vm.EditingEntry.Name = "Unsaved";
        vm.InlinePasswordInput = "discard me";
        vm.SelectedConnection = b;
        vm.NewConnection();
        vm.EditingEntry.Name = "Unsaved new";
        vm.ClearSessionDrafts();
        var reopened = CreateVm();
        await reopened.InitializeAsync();
        Assert.Equal("Persisted", (await _repository.GetConnectionByIdAsync(a.Id))!.Name);
        Assert.Equal(2, reopened.FilteredConnections.Count);
        Assert.Empty(reopened.NewDrafts);
        Assert.False(reopened.HasPendingChanges);
        Assert.Empty(reopened.InlinePasswordInput);
    }

    [Fact]
    public Task DispatcherBoundSaveAndNavigation_NeverPublishCollectionsOffThread()
        => StaTest.RunAsync(async () =>
        {
            var (vm, a, b) = await PairAsync();
            var thread = Environment.CurrentManagedThreadId;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.FilteredConnections) or nameof(MainViewModel.EditingEntry))
                    Assert.Equal(thread, Environment.CurrentManagedThreadId);
            };
            for (var i = 0; i < 12; i++)
            {
                vm.SelectedConnection = vm.FilteredConnections.Single(c => c.Id == a.Id);
                vm.EditingEntry.Notes = $"edit {i}";
                Assert.True(await vm.SaveConnectionAsync());
                vm.SelectedConnection = vm.FilteredConnections.Single(c => c.Id == b.Id);
                Assert.Equal(b.Id, vm.EditingEntry.Id);
                Assert.Equal(2, vm.FilteredConnections.Count);
                await vm.SetViewModeAsync(ConnectionViewMode.Favorites);
                await vm.SetViewModeAsync(ConnectionViewMode.Recent);
                await vm.SetViewModeAsync(ConnectionViewMode.Trash);
                await vm.SetViewModeAsync(ConnectionViewMode.All);
            }
        });

    private sealed class ControlledRepository(AppPaths paths, AppLogger logger) : SqliteRepository(paths, logger)
    {
        public bool FailSave { get; set; }
        public bool FailReload { get; set; }
        public TaskCompletionSource SaveEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _continue;
        public void PauseSave() { SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); _continue = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ContinueSave() => _continue!.TrySetResult();
        public override async Task SaveConnectionWithCredentialAsync(ConnectionEntry entry, SavedCredential? credential)
        {
            if (_continue != null) { SaveEntered.TrySetResult(); await _continue.Task; _continue = null; }
            if (FailSave) throw new IOException("injected");
            await base.SaveConnectionWithCredentialAsync(entry, credential);
        }
        public override Task<List<SavedCredential>> GetAllCredentialsAsync()
            => FailReload ? Task.FromException<List<SavedCredential>>(new IOException("injected")) : base.GetAllCredentialsAsync();
    }

    private sealed class ControlledVault(SettingsService settings, SqliteRepository repository, AppLogger logger)
        : CredentialVaultService(settings, repository, logger, useWindowsVault: false)
    {
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }
        public List<string> Deleted { get; } = new();
        public override void WriteToWindowsVault(SavedCredential credential)
        {
            if (FailWrite) throw new IOException("injected");
        }
        public override void RemoveFromWindowsVault(string id)
        {
            if (FailDelete) throw new IOException("injected");
            Deleted.Add(id);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch (IOException) { }
    }
}
