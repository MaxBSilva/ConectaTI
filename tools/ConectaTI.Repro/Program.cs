using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

// Runs unchanged against both 1.2.0 and the fixed assembly, using fictional data.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(async () =>
        {
            var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ConectaTI_Repro_" + Guid.NewGuid().ToString("N")));
            try
            {
                var logger = new AppLogger(paths);
                var settings = new SettingsService(paths, logger);
                settings.Load();
                var repository = new SqliteRepository(paths, logger);
                repository.InitializeAndMigrate();
                var vault = new CredentialVaultService(settings, repository, logger, false);
                var clipboard = new ClipboardService(logger);
                var a = new ConnectionEntry { Name = "Alpha", ClientName = "Ficticio", Host = "10.0.0.1" };
                var b = new ConnectionEntry { Name = "Beta", ClientName = "Ficticio", Host = "10.0.0.2" };
                await repository.SaveConnectionsBatchAsync([a, b]);
                var vm = new MainViewModel(paths, logger, settings, repository, vault, clipboard,
                    new ConnectionLauncherService(paths, settings, repository, vault, clipboard, logger),
                    new ImportExportService(repository, vault, logger), new NetworkDiagnosticService());
                await vm.InitializeAsync();
                var list = new ListBox { ItemsSource = vm.FilteredConnections };
                Console.WriteLine($"before: items={list.Items.Count}, selectedMatchesForm={vm.SelectedConnection?.Id == vm.EditingEntry.Id}");
                vm.EditingEntry.Name = "Edited Alpha";
                var callbackCalls = 0;
                vm.ConfirmUnsavedChangesCallback = _ => { callbackCalls++; return UnsavedChangesChoice.Save; };
                try { vm.SelectedConnection = b; Console.WriteLine("navigation: completed"); }
                catch (Exception ex) { Console.WriteLine($"navigation: {ex.GetType().Name}; detail={ex.Message}"); }
                Console.WriteLine($"after: sourceItems={vm.FilteredConnections.Count}, prompts={callbackCalls}, selectedMatchesForm={vm.SelectedConnection?.Id == vm.EditingEntry.Id}");
                Console.WriteLine($"sqliteRows={(await repository.GetAllConnectionsAsync()).Count}");
                GC.KeepAlive(list);
            }
            catch (Exception ex) { Console.WriteLine($"harness: {ex.GetType().Name}; {ex.Message}"); Environment.ExitCode = 1; }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(paths.RootDirectory, true); } catch (IOException) { }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }
}
