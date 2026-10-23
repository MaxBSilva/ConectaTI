using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using ConectaTI.App;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

// Runs the real window with isolated fictional data, bypassing legacy-data discovery.
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ProductionApp.xaml"))
            .Replace("clr-namespace:ConectaTI.App.Helpers", "clr-namespace:ConectaTI.App.Helpers;assembly=ConectaTI");
        var dictionary = XDocument.Parse(source).Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "helpers", "clr-namespace:ConectaTI.App.Helpers;assembly=ConectaTI");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var paths = new AppPaths(Path.Combine(AppContext.BaseDirectory, "fictional-data"));
                var logger = new AppLogger(paths);
                var settings = new SettingsService(paths, logger);
                settings.Load();
                var repository = new SqliteRepository(paths, logger);
                await Task.Run(repository.InitializeAndMigrate);
                if ((await repository.GetAllConnectionsAsync(true)).Count == 0)
                    await repository.SaveConnectionsBatchAsync([
                        new ConnectionEntry { Id = "ui-alpha", Name = "Alpha fictício", Host = "alpha.example.test", ClientName = "Cliente teste", IsFavorite = true },
                        new ConnectionEntry { Id = "ui-beta", Name = "Beta fictício", Host = "beta.example.test", ClientName = "Cliente teste", LastOpenedAtUtc = DateTime.UtcNow },
                        new ConnectionEntry { Id = "ui-trash", Name = "Lixeira fictícia", Host = "trash.example.test", ClientName = "Cliente teste", IsDeleted = true, DeletedAtUtc = DateTime.UtcNow }
                    ]);
                var vault = new CredentialVaultService(settings, repository, logger, useWindowsVault: false);
                var clipboard = new ClipboardService(logger);
                var vm = new MainViewModel(paths, logger, settings, repository, vault, clipboard,
                    new ConnectionLauncherService(paths, settings, repository, vault, clipboard, logger),
                    new ImportExportService(repository, vault, logger), new NetworkDiagnosticService());
                await vm.InitializeAsync();
                var window = new MainWindow(vm) { Title = "ConectaTI — Teste isolado", Width = 1000, Height = 760 };
                app.MainWindow = window;
                window.Closed += (_, _) => app.Shutdown();
                window.Show();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke-error.txt"), ex.ToString());
                app.Shutdown(1);
            }
        });
        Dispatcher.Run();
    }

}
