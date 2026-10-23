using System.Windows;
using ConectaTI.App.Data;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

namespace ConectaTI.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        if (!paths.IsDataDirectoryWritable)
        {
            var res = MessageBox.Show(
                $"A pasta do aplicativo ('{paths.RootDirectory}') não possui permissão de gravação de arquivos.\n\nDeseja selecionar uma pasta com permissão de escrita para armazenar o banco de dados e as configurações?",
                "ConectaTI — Permissão de Pasta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Selecione a pasta de dados do ConectaTI"
                };
                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                {
                    AppPaths.SaveDataLocationPointer(dialog.FolderName);
                    paths = new AppPaths(dialog.FolderName);
                }
            }
        }

        var logger = new AppLogger(paths);
        logger.Info("Startup", "Iniciando ConectaTI v1.2.1...");

        // Migração segura e consistente de bases legadas (%LOCALAPPDATA%\ConectaTI\conectati.db)
        DatabaseMigrationManager.EnsureDatabaseMigrated(paths, logger);

        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled", "Exceção não tratada na interface WPF.", args.Exception);
            MessageBox.Show(
                $"Ocorreu um erro inesperado:\n{args.Exception.Message}",
                "ConectaTI — Erro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var settingsService = new SettingsService(paths, logger);
        var settings = settingsService.Load();
        ExternalClientDetector.AutoPopulateMissingPaths(settings);
        settingsService.Save();

        ThemeService.ApplyTheme(settings.Theme);

        var repository = new SqliteRepository(paths, logger);
        var vaultService = new CredentialVaultService(settingsService, repository, logger);
        var clipboardService = new ClipboardService(logger);
        var launcherService = new ConnectionLauncherService(paths, settingsService, repository, vaultService, clipboardService, logger);
        var importExportService = new ImportExportService(repository, vaultService, logger);
        var diagnosticService = new NetworkDiagnosticService();

        var mainVm = new MainViewModel(
            paths,
            logger,
            settingsService,
            repository,
            vaultService,
            clipboardService,
            launcherService,
            importExportService,
            diagnosticService);

        // Preserva integralmente a base de dados existente do usuário sem recarregar exemplos fictícios
        await mainVm.InitializeAsync(seedDemoIfEmpty: false);

        var mainWindow = new MainWindow(mainVm);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            WindowsCredentialVaultInterop.CleanupAllManagedRdpCredentials();
        }
        catch
        {
        }
        base.OnExit(e);
    }
}
