using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using ConectaTI.App.Data;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Views;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class StartupShortcutsAndCredentialsCopyTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Features_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void StartupWebsites_ValidatesHttpHttps_AutoPrefixesHttps_DeduplicatesAndRejectsLocalPaths()
    {
        Assert.True(StartupWebsitesService.TryNormalizeHttpUrl("painel.empresa.example.test", out var url1));
        Assert.Equal("https://painel.empresa.example.test", url1);

        Assert.True(StartupWebsitesService.TryNormalizeHttpUrl("http://intranet:8080/chamados", out var url2));
        Assert.Equal("http://intranet:8080/chamados", url2);

        Assert.False(StartupWebsitesService.TryNormalizeHttpUrl(@"C:\Windows\System32\calc.exe", out _));
        Assert.False(StartupWebsitesService.TryNormalizeHttpUrl(@"\\servidor\compartilhamento", out _));
        Assert.False(StartupWebsitesService.TryNormalizeHttpUrl("javascript:alert(1)", out _));
        Assert.False(StartupWebsitesService.TryNormalizeHttpUrl("file:///C:/teste.html", out _));
        Assert.False(StartupWebsitesService.TryNormalizeHttpUrl("   ", out _));

        var settings = new AppSettings
        {
            OpenStartupWebsites = true,
            StartupWebsite1 = "chamados.empresa.example.test",
            StartupWebsite2 = "https://chamados.empresa.example.test",
            StartupWebsite3 = "https://monitor.empresa.example.test/painel"
        };

        var launched = new List<string>();
        var count = StartupWebsitesService.LaunchConfiguredWebsites(settings, browserLauncherOverride: launched.Add);
        Assert.Equal(2, count);
        Assert.Equal(
            ["https://chamados.empresa.example.test", "https://monitor.empresa.example.test/painel"],
            launched);

        Assert.True(StartupWebsitesService.IsWindowsStartupInvocation(["--windows-startup"]));
        Assert.False(StartupWebsitesService.IsWindowsStartupInvocation(["--data-dir", "C:\\Temp"]));
        Assert.Contains(StartupWebsitesService.WindowsStartupArg, HotKeyTextService.BuildStartupRegistryValue(@"C:\Test\ConectaTI.exe"));
    }

    [Fact]
    public void QuickTextsTrigger_DefaultsToScrollLock_SupportsCustomTriggerAndPersistsInXml()
    {
        var quickTextsDir = Path.Combine(_paths.RootDirectory, "HotKeyTextTrigger");
        var prefix = "CTI_Trig_" + Guid.NewGuid().ToString("N")[..8] + "_";
        var runName = "ConectaTI_Test_" + Guid.NewGuid().ToString("N")[..8];

        using (var service = new HotKeyTextService(quickTextsDir, prefix, runName, installGlobalHooks: false))
        {
            Assert.Equal("Scroll Lock", service.EffectiveTriggerDisplay);
            Assert.Equal(0x91u, service.TriggerVirtualKey);

            // Rejeita tecla de digitação comum sem Ctrl/Alt para não travar a digitação global
            Assert.False(service.SetTriggerShortcut("A"));
            Assert.False(service.SetTriggerShortcut("1"));
            Assert.False(service.SetTriggerShortcut("Espaço"));
            Assert.Equal("Scroll Lock", service.EffectiveTriggerDisplay);

            // Aceita tecla dedicada (Pause, Insert, F9) e combinação com Ctrl/Alt (Ctrl+Espaço)
            Assert.True(service.SetTriggerShortcut("Pause"));
            Assert.Equal("Pause", service.EffectiveTriggerDisplay);
            Assert.Equal(0x13u, service.TriggerVirtualKey);

            Assert.True(service.SetTriggerShortcut("Ctrl+Espaço"));
            Assert.Equal("Ctrl+Espaço", service.EffectiveTriggerDisplay);
            Assert.True(service.TriggerRequiresModifiers);
        }

        using (var reopened = new HotKeyTextService(quickTextsDir, prefix, runName, installGlobalHooks: false))
        {
            Assert.Equal("Ctrl+Espaço", reopened.EffectiveTriggerDisplay);
            reopened.ResetTriggerShortcutToDefault();
            Assert.Equal("Scroll Lock", reopened.EffectiveTriggerDisplay);
            Assert.Equal(string.Empty, reopened.Config.AtalhoAtivacao);
        }
    }

    [Fact]
    public void CustomizableShortcuts_PreserveDefaults_RejectConflictsAndReservedKeys_AndPersist()
    {
        var logger = new AppLogger(_paths);
        var settingsService = new SettingsService(_paths, logger);
        var s = settingsService.Load();

        var defaults = KeyboardShortcutsCatalog.GetAll(s);
        Assert.Equal(11, defaults.Count);
        Assert.Equal("Ctrl+N", KeyboardShortcutsCatalog.GetEffectiveShortcut(s, KeyboardShortcutsCatalog.ActionNewConnection));
        Assert.Equal("Ctrl+S", KeyboardShortcutsCatalog.GetEffectiveShortcut(s, KeyboardShortcutsCatalog.ActionSaveConnection));

        // Rejeita atalhos reservados de edição e teclas simples sem modificador
        Assert.False(KeyboardShortcutsCatalog.TryNormalizeShortcutString("Ctrl+C", out _));
        Assert.False(KeyboardShortcutsCatalog.TryNormalizeShortcutString("Ctrl+V", out _));
        Assert.False(KeyboardShortcutsCatalog.TryNormalizeShortcutString("N", out _));

        // Impede conflito com atalho já usado por outra ação (Ctrl+S)
        Assert.False(KeyboardShortcutsCatalog.TryAssignShortcut(s, KeyboardShortcutsCatalog.ActionNewConnection, "Ctrl+S", out var conflict));
        Assert.False(string.IsNullOrWhiteSpace(conflict));

        // Permite personalizar e persistir no settings.json
        Assert.True(KeyboardShortcutsCatalog.TryAssignShortcut(s, KeyboardShortcutsCatalog.ActionNewConnection, "Ctrl+Shift+N", out _));
        Assert.True(KeyboardShortcutsCatalog.TryAssignShortcut(s, KeyboardShortcutsCatalog.ActionFocusConnectionName, "F4", out _));
        settingsService.Save();

        var reloaded = new SettingsService(_paths, logger).Load();
        Assert.Equal("Ctrl+Shift+N", KeyboardShortcutsCatalog.GetEffectiveShortcut(reloaded, KeyboardShortcutsCatalog.ActionNewConnection));
        Assert.Equal("F4", KeyboardShortcutsCatalog.GetEffectiveShortcut(reloaded, KeyboardShortcutsCatalog.ActionFocusConnectionName));
        Assert.True(KeyboardShortcutsCatalog.TryMatchAction(reloaded, Key.N, ModifierKeys.Control | ModifierKeys.Shift, out var matched));
        Assert.Equal(KeyboardShortcutsCatalog.ActionNewConnection, matched);

        KeyboardShortcutsCatalog.ResetAllShortcuts(reloaded);
        Assert.Equal("Ctrl+N", KeyboardShortcutsCatalog.GetEffectiveShortcut(reloaded, KeyboardShortcutsCatalog.ActionNewConnection));
        Assert.Equal("F2", KeyboardShortcutsCatalog.GetEffectiveShortcut(reloaded, KeyboardShortcutsCatalog.ActionFocusConnectionName));
    }

    [Fact]
    public Task CredentialsDialog_CopiesFullAccessInfoUserAndSavedPassword_AndGuardsSelectionChanges()
        => StaTest.RunAsync(async () =>
        {
            LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
            var logger = new AppLogger(_paths);
            var settingsService = new SettingsService(_paths, logger);
            settingsService.Load();
            settingsService.Current.ClipboardClearSeconds = 25;

            var repo = new SqliteRepository(_paths, logger);
            repo.InitializeAndMigrate();
            var vault = new CredentialVaultService(settingsService, repo, logger, useWindowsVault: false);
            VaultTestSupport.Initialize(vault);

            string? lastClipboardText = null;
            var clipboard = new ClipboardService(
                logger,
                txt => { lastClipboardText = txt; return true; },
                () => lastClipboardText,
                () => lastClipboardText = null);

            var cred = new SavedCredential
            {
                Name = "Suporte Homologação",
                Username = "operador.ficticio",
                Domain = "CORP",
                Notes = "Uso exclusivo em homologação"
            };
            await vault.SaveApplicationCredentialAsync(cred, "SenhaFicticia#2026");

            var dialog = new CredentialsDialog(repo, vault, clipboard, settingsService);
            try
            {
                dialog.Show();
                var list = (ListBox)dialog.FindName("CredentialsListBox");
                for (var n = 0; list.Items.Count < 1 && n < 100; n++)
                    await Task.Delay(10);

                Assert.Single(list.Items);
                list.SelectedIndex = 0;

                Assert.True(await dialog.CopySelectedCredentialUserAsync());
                Assert.Equal(@"CORP\operador.ficticio", lastClipboardText);

                Assert.True(await dialog.CopySelectedCredentialPasswordAsync());
                Assert.Equal("SenhaFicticia#2026", lastClipboardText);

                Assert.True(await dialog.CopySelectedCredentialAccessAsync());
                Assert.NotNull(lastClipboardText);
                Assert.Contains("Credencial: Suporte Homologação", lastClipboardText);
                Assert.Contains(@"Usuário: CORP\operador.ficticio", lastClipboardText);
                Assert.Contains("Senha: SenhaFicticia#2026", lastClipboardText);
                Assert.Contains("Observações: Uso exclusivo em homologação", lastClipboardText);
            }
            finally
            {
                dialog.Close();
            }
        });

    public void Dispose()
    {
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch { }
    }
}
