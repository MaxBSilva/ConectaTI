using System.Diagnostics;
using System.IO;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;

// Isolated, fictional, loopback-only client inspection. Never discovers user databases.
var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ConectaTI-TitleSmoke-" + Guid.NewGuid().ToString("N")));
paths.EnsureDirectories();
var logger = new AppLogger(paths);
var settings = new SettingsService(paths, logger); settings.Load();
var repository = new SqliteRepository(paths, logger); repository.InitializeAndMigrate();
var vault = new CredentialVaultService(settings, repository, logger, useWindowsVault: false);
var launcher = new ConnectionLauncherService(paths, settings, repository, vault, new ClipboardService(logger), logger);
foreach (var name in new[] { "ERP Produção fictício", "HCM fictício" })
{
    var entry = new ConnectionEntry { Name = name, Host = "127.0.0.1", Port = 65534, RdpFullScreen = false, RdpWidth = 1024, RdpHeight = 768 };
    var plan = launcher.BuildLaunchPlan(entry);
    Directory.CreateDirectory(Path.GetDirectoryName(plan.TempRdpFilePath!)!);
    File.WriteAllText(plan.TempRdpFilePath!, plan.TempRdpContent, new UTF8Encoding(true));
    Console.WriteLine(plan.TempRdpFilePath);
    if (args.Contains("--edit") || args.Contains("--connect"))
    {
        var start = new ProcessStartInfo(plan.ExecutablePath) { UseShellExecute = false };
        if (args.Contains("--edit")) start.ArgumentList.Add("/edit");
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start); Console.WriteLine($"PID: {process?.Id}");
    }
}
