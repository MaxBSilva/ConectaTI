using System.IO;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class RefactorDiagnosticRaceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task DiagnosticCompletionFromPreviousSelectionCannotOverwriteCurrentPanel(bool fail)
        => StaTest.RunAsync(async () =>
        {
            var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ConectaTI_Diagnostic_" + Guid.NewGuid().ToString("N")));
            try
            {
                var logger = new AppLogger(paths); var repo = new SqliteRepository(paths, logger); repo.InitializeAndMigrate();
                var settings = new SettingsService(paths, logger); settings.Load();
                var vault = new CredentialVaultService(settings, repo, logger, false);
                var clip = new ClipboardService(logger, _ => true, () => null, () => { });
                var probe = new DelayedProbe();
                var a = new ConnectionEntry { Name = "Alpha", Host = "a.example.test" };
                var b = new ConnectionEntry { Name = "Beta", Host = "b.example.test", IsActive = false };
                await repo.SaveConnectionsBatchAsync([a, b]);
                var vm = new MainViewModel(paths, logger, settings, repo, vault, clip,
                    new(paths, settings, repo, vault, logger), new(repo, vault, logger), probe);
                await vm.InitializeAsync(); vm.SelectedConnection = a;
                var pending = vm.RunDiagnosticAsync();
                await probe.Entered.Task;
                vm.SelectedConnection = b; vm.StatusMessage = "CURRENT B";
                if (fail) probe.Completion.SetException(new IOException("Synthetic network failure"));
                else probe.Completion.SetResult(new(true, true, [], 1, 1, "OLD A", "OLD A DETAILS"));
                await pending;
                Assert.Equal("CURRENT B", vm.StatusMessage);
                Assert.Empty(vm.DiagnosticResultTitle); Assert.Empty(vm.DiagnosticResultDetails);
                Assert.False(vm.IsRunningDiagnostic); Assert.False(vm.EditingEntry.IsActive);
            }
            finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(paths.RootDirectory, true); } catch { } }
        });

    private sealed class DelayedProbe : INetworkConnectivityProbe
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource<NetworkDiagnosticResult> Completion { get; } = new();
        public Task<NetworkDiagnosticResult> TestConnectivityAsync(string hostInput, int port, int timeoutMs = 4000, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return Completion.Task; }
    }
}
