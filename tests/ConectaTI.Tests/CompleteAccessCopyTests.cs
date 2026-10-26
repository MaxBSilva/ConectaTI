using System.IO;
using System.Runtime.InteropServices;
using ConectaTI.App.Data;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

namespace ConectaTI.Tests;

public sealed class CompleteAccessCopyTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Copy_" + Guid.NewGuid().ToString("N")));
    private readonly SqliteRepository _repo;
    private readonly CredentialVaultService _vault;
    private readonly MainViewModel _vm;
    private string _clipboard = "original";
    private int _starts;
    private int _writes;
    private bool _clipboardUnavailable;
    private Action? _duringWrite;

    public CompleteAccessCopyTests()
    {
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        var logger = new AppLogger(_paths);
        var settings = new SettingsService(_paths, logger);
        _repo = new SqliteRepository(_paths, logger);
        _repo.InitializeAndMigrate();
        _vault = new CredentialVaultService(settings, _repo, logger, useWindowsVault: false);
        var clipboard = new ClipboardService(logger, text =>
        {
            _writes++;
            _duringWrite?.Invoke();
            if (_clipboardUnavailable) throw new COMException("Synthetic clipboard contention");
            _clipboard = text;
            return true;
        }, () => _clipboard, () => _clipboard = string.Empty);
        _vm = new MainViewModel(_paths, logger, settings, _repo, _vault, clipboard,
            new ConnectionLauncherService(_paths, settings, _repo, _vault, clipboard, logger, _ => { _starts++; return null; }),
            new ImportExportService(_repo, _vault, logger), new NetworkDiagnosticService());
    }

    private async Task<(ConnectionEntry A, ConnectionEntry B)> SeedAsync(string host = "servidor.example.test", int port = 3389)
    {
        var credentialA = new SavedCredential { Name = "Credencial fictícia Alpha", Username = "operador", Domain = "EXEMPLO" };
        var credentialB = new SavedCredential { Name = "Credencial fictícia Beta", Username = "outro", Domain = "BETA" };
        await _vault.SaveApplicationCredentialAsync(credentialA, "SenhaAlpha#SóTeste");
        await _vault.SaveApplicationCredentialAsync(credentialB, "SenhaBeta#SóTeste");
        var a = new ConnectionEntry { Name = "Acesso Alpha", Host = host, Port = port, CredentialId = credentialA.Id, Username = "operador", Domain = "EXEMPLO" };
        var b = new ConnectionEntry { Name = "Acesso Beta", Host = "192.0.2.20", Port = 3391, CredentialId = credentialB.Id, Username = "outro", Domain = "BETA" };
        await _repo.SaveConnectionsBatchAsync([a, b]);
        await _vm.InitializeAsync();
        _vm.SelectedConnection = _vm.FilteredConnections.Single(entry => entry.Id == a.Id);
        return (a, b);
    }

    [Fact]
    public async Task HiddenPassword_CopiesExactPlainText_WithoutSavingConnectingRevealingOrLoggingContents()
    {
        var (a, _) = await SeedAsync();
        Assert.False(_vm.IsPasswordRevealed);
        Assert.Empty(_vm.InlinePasswordInput);
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal("Nome: Acesso Alpha\r\nEndereço: servidor.example.test\r\nUsuário: EXEMPLO\\operador\r\nSenha: SenhaAlpha#SóTeste", _clipboard);
        Assert.Equal("Dados do acesso copiados", _vm.StatusMessage);
        Assert.False(_vm.IsPasswordRevealed);
        Assert.Empty(_vm.RevealedPasswordText);
        Assert.False(_vm.HasPendingChanges);
        Assert.Equal(0, _starts);
        Assert.Null((await _repo.GetConnectionByIdAsync(a.Id))!.LastOpenedAtUtc);
        foreach (var log in Directory.GetFiles(_paths.LogsDirectory))
        {
            var content = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain("SenhaAlpha#SóTeste", content);
            Assert.DoesNotContain(_clipboard, content);
        }
    }

    [Fact]
    public async Task PendingEdits_AgreeWithIndividualCopies_AndStayUnsaved()
    {
        var (a, _) = await SeedAsync();
        _vm.EditingEntry.Name = "Rascunho São José";
        _vm.InlineHostAndPort = "erp.example.test:3389";
        _vm.InlineUserWithDomain = "NOVO\\tecnico";
        _vm.InlinePasswordInput = "SenhaPendente#SóTeste";
        await _vm.CopyConnectionNameAsync(); var name = _clipboard;
        await _vm.CopyHostAsync(); var address = _clipboard;
        await _vm.CopyUsernameAsync(); var user = _clipboard;
        await _vm.CopyPasswordAsync(); var password = _clipboard;
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal($"Nome: {name}\r\nEndereço: {address}\r\nUsuário: {user}\r\nSenha: {password}", _clipboard);
        Assert.True(_vm.HasPendingChanges);
        var stored = (await _repo.GetConnectionByIdAsync(a.Id))!;
        Assert.Equal("Acesso Alpha", stored.Name);
        Assert.Equal("servidor.example.test", stored.Host);
        Assert.Equal("SenhaAlpha#SóTeste", await _vault.ReadSecretAsync((await _repo.GetCredentialByIdAsync(stored.CredentialId!))!));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task SelectingAnotherAccess_CopiesOnlyItsOwnFieldsAndSecret()
    {
        var (_, b) = await SeedAsync();
        _vm.EditingEntry.Name = "Rascunho Alpha";
        _vm.InlinePasswordInput = "SenhaRascunhoAlpha";
        _vm.SelectedConnection = _vm.FilteredConnections.Single(entry => entry.Id == b.Id);
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal("Nome: Acesso Beta\r\nEndereço: 192.0.2.20:3391\r\nUsuário: BETA\\outro\r\nSenha: SenhaBeta#SóTeste", _clipboard);
        Assert.DoesNotContain("Alpha", _clipboard);
    }

    [Theory]
    [InlineData("servidor.example.test", 3389, "servidor.example.test")]
    [InlineData("servidor.example.test", 3391, "servidor.example.test:3391")]
    [InlineData("192.0.2.11", 3389, "192.0.2.11")]
    [InlineData("192.0.2.11:3391", 3389, "192.0.2.11:3391")]
    [InlineData("2001:db8::1", 3391, "[2001:db8::1]:3391")]
    public async Task Addresses_UseRegisteredHostAndConfiguredPort_WithoutDuplication(string host, int port, string expected)
    {
        await SeedAsync(host, port);
        await _vm.CopyCompleteAccessAsync();
        Assert.Contains($"\r\nEndereço: {expected}\r\n", _clipboard);
        await _vm.CopyHostAsync();
        Assert.Equal(expected, _clipboard);
    }

    [Fact]
    public async Task NoSelection_DisablesCommand_AndCopiesNothingIncludingNewDrafts()
    {
        await SeedAsync();
        _vm.SelectedConnection = null;
        Assert.False(_vm.CopyCompleteAccessCommand.CanExecute(null));
        await _vm.CopyCompleteAccessAsync();
        _vm.NewConnection();
        _vm.EditingEntry.Name = "Novo rascunho";
        Assert.False(_vm.CopyCompleteAccessCommand.CanExecute(null));
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal("original", _clipboard);
        Assert.Equal(0, _writes);
    }

    [Fact]
    public async Task SelectionChangeDuringSecretRecovery_CancelsOldCopy()
    {
        var (_, b) = await SeedAsync();
        await _repo.CredentialOperationGate.WaitAsync();
        Task copy;
        try
        {
            copy = _vm.CopyCompleteAccessAsync();
            _vm.SelectedConnection = _vm.FilteredConnections.Single(entry => entry.Id == b.Id);
        }
        finally { _repo.CredentialOperationGate.Release(); }
        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("original", _clipboard);
        Assert.Equal(b.Id, _vm.EditingEntry.Id);
        await _vm.CopyCompleteAccessAsync();
        Assert.Contains("Senha: SenhaBeta#SóTeste", _clipboard);
    }

    [Fact]
    public async Task RawFieldEditDuringRecovery_CancelsCopyEvenWhenNormalizedIdentityIsUnchanged()
    {
        await SeedAsync();
        await _repo.CredentialOperationGate.WaitAsync();
        Task copy;
        try
        {
            copy = _vm.CopyCompleteAccessAsync();
            _vm.InlineUserWithDomain = "  EXEMPLO\\operador  ";
        }
        finally { _repo.CredentialOperationGate.Release(); }
        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("original", _clipboard);
    }

    [Fact]
    public async Task SelectionChangeDuringClipboardRetry_CancelsOldCopy()
    {
        var (_, b) = await SeedAsync();
        _clipboardUnavailable = true;
        _duringWrite = () => _vm.SelectedConnection = _vm.FilteredConnections.Single(entry => entry.Id == b.Id);
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal(1, _writes);
        Assert.Equal("original", _clipboard);
    }

    [Fact]
    public async Task ExplicitRemoval_IsConsistentWithPasswordCopy_AndDoesNotExposeStoredPassword()
    {
        await SeedAsync();
        _vm.MarkPasswordForRemoval();
        await _vm.CopyCompleteAccessAsync();
        Assert.EndsWith("Senha: ", _clipboard);
        _clipboard = "unchanged";
        await _vm.CopyPasswordAsync();
        Assert.Equal("unchanged", _clipboard);
        Assert.True(_vm.HasPendingChanges);
    }

    [Fact]
    public async Task UnreadableCredential_DoesNotCopyPartialOrForeignData()
    {
        var (a, _) = await SeedAsync();
        var credential = (await _repo.GetCredentialByIdAsync(a.CredentialId!))!;
        credential.ProtectedSecretBase64 = "DPAPI1:invalid!";
        await _repo.SaveCredentialAsync(credential);
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal("original", _clipboard);
        Assert.Equal(0, _writes);
        Assert.True(_vm.PasswordRecoveryFailed);
        Assert.DoesNotContain("SenhaAlpha", _vm.StatusMessage);
    }

    [Fact]
    public async Task ClipboardFailure_IsBoundedAndReported_WithoutThrowingOrLeakingSecret()
    {
        await SeedAsync();
        _clipboardUnavailable = true;
        await _vm.CopyCompleteAccessAsync();
        Assert.Equal(6, _writes);
        Assert.Equal("original", _clipboard);
        Assert.Equal("Não foi possível copiar para a área de transferência.", _vm.StatusMessage);
        Assert.DoesNotContain("SenhaAlpha", _vm.StatusMessage);
    }

    public void Dispose()
    {
        _vm.ClearSessionDrafts();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_paths.RootDirectory, recursive: true);
    }
}
