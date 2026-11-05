using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConectaTI.App.Data;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class Evolution180Tests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_180_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void KeyboardShortcutsCatalog_ProvidesLocalizedShortcutsWithoutGlobalSecretShortcuts()
    {
        try
        {
            foreach (var lang in new[] { AppLanguage.Portuguese, AppLanguage.English, AppLanguage.Spanish })
            {
                LocalizationService.Current.SetLanguage(lang);
                var items = KeyboardShortcutsCatalog.GetAll();
                Assert.Equal(11, items.Count);
                Assert.Contains(items, i => i.Shortcut == "Ctrl+N");
                Assert.Contains(items, i => i.Shortcut == "Ctrl+S");
                Assert.Contains(items, i => i.Shortcut == "Ctrl+F");
                Assert.Contains(items, i => i.Shortcut == "F1");
                Assert.Contains(items, i => i.Shortcut == "F2");
                Assert.Contains(items, i => i.Shortcut == "Ctrl+,");
                Assert.Contains(items, i => i.Shortcut == "Enter");
                Assert.Contains(items, i => i.Shortcut == "Esc");

                foreach (var item in items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Action));
                    Assert.False(string.IsNullOrWhiteSpace(item.Context));
                    Assert.False(string.IsNullOrWhiteSpace(item.Description));
                    Assert.DoesNotContain("Text", item.Action);
                    Assert.DoesNotContain("Text", item.Context);
                    Assert.DoesNotContain("Text", item.Description);
                    Assert.DoesNotContain("copiar senha", item.Action, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("copy password", item.Action, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        }
    }

    [Fact]
    public Task ViewPreferences_FontSizeAndDensityOperateIndependently()
        => StaTest.RunAsync(() =>
        {
            var settings = new AppSettings { FontSize = 18, ComfortableMode = false };
            ViewPreferencesService.Apply(settings);
            var resources = Application.Current!.Resources;
            Assert.Equal(18d, (double)resources["AppFontSize"]);
            Assert.Equal(new Thickness(4, 2, 4, 2), (Thickness)resources["ConnectionItemPadding"]);
            Assert.Equal(new Thickness(6, 3, 6, 3), (Thickness)resources["InputPadding"]);

            settings.FontSize = 11;
            settings.ComfortableMode = true;
            ViewPreferencesService.Apply(settings);
            Assert.Equal(11d, (double)resources["AppFontSize"]);
            Assert.Equal(new Thickness(6, 7, 6, 7), (Thickness)resources["ConnectionItemPadding"]);
            Assert.Equal(new Thickness(8, 6, 8, 6), (Thickness)resources["InputPadding"]);

            settings.FontSize = 12.5;
            settings.ComfortableMode = false;
            ViewPreferencesService.Apply(settings);
            return Task.CompletedTask;
        });

    [Fact]
    public Task EscapeKeyInEditor_PreservesSearchFilterAndDraft_WhileSettingsAndAdaptiveHeaderWork()
        => StaTest.RunAsync(async () =>
        {
            var logger = new AppLogger(_paths);
            var settingsService = new SettingsService(_paths, logger);
            settingsService.Load();
            var repo = new SqliteRepository(_paths, logger);
            repo.InitializeAndMigrate();
            await repo.SaveConnectionAsync(new ConnectionEntry { Id = "srv-1", Name = "SRV-ALFA", Host = "10.0.0.1", ClientName = "Cliente A" });
            await repo.SaveConnectionAsync(new ConnectionEntry { Id = "srv-2", Name = "SRV-BETA", Host = "10.0.0.2", ClientName = "Cliente B" });

            var vault = new CredentialVaultService(settingsService, repo, logger, useWindowsVault: false);
            VaultTestSupport.Initialize(vault);
            var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });
            var launcher = new ConnectionLauncherService(_paths, settingsService, repo, vault, clipboard, logger);
            var importExport = new ImportExportService(repo, vault, logger);
            var vm = new MainViewModel(_paths, logger, settingsService, repo, vault, clipboard, launcher, importExport, new NetworkDiagnosticService());
            await vm.InitializeAsync();

            var window = new ConectaTI.App.MainWindow(vm);
            window.Show();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

            try
            {
                // 1. Filtro de busca ativo + edição no formulário direito: Esc não deve limpar a busca nem apagar o rascunho
                vm.SearchText = "SRV";
                var nameBox = (TextBox)window.FindName("ConnectionNameTextBox");
                var searchBox = (TextBox)window.FindName("SearchTextBox");
                nameBox.Focus();
                nameBox.Text = "SRV-ALFA Rascunho";
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

                Assert.Equal("SRV", vm.SearchText);
                Assert.True(vm.HasPendingChanges);

                var escInEditor = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                nameBox.RaiseEvent(escInEditor);

                Assert.Equal("SRV", vm.SearchText);
                Assert.Equal("SRV-ALFA Rascunho", vm.EditingEntry.Name);
                Assert.True(vm.HasPendingChanges);

                // 2. Com foco no campo Buscar, Esc limpa apenas o filtro de busca sem descartar o rascunho
                searchBox.Focus();
                var escInSearch = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                searchBox.RaiseEvent(escInSearch);

                Assert.Equal(string.Empty, vm.SearchText);
                Assert.Equal("SRV-ALFA Rascunho", vm.EditingEntry.Name);
                Assert.True(vm.HasPendingChanges);

                // 3. Adaptação responsiva da barra superior em janela estreita (480px com fonte 18) vs larga (1000px com fonte 12.5)
                settingsService.Current.FontSize = 18;
                window.UpdateAdaptiveHeaderAndPaneBounds(480);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("ImportRdpButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("BatchEditButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("TopOverflowButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ThemeButtonLabel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("SettingsButtonLabel")).Visibility);

                settingsService.Current.FontSize = 12.5;
                window.UpdateAdaptiveHeaderAndPaneBounds(1000);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("ImportRdpButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("BatchEditButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ThemeButtonLabel")).Visibility);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("SettingsButtonLabel")).Visibility);

                // 4. Janela de Configurações com abas separadas (Geral, Aparência, Programas, Credenciais, Importação e backup, Atalhos, Sobre e diagnóstico)
                var settingsDlg = new ExecutablesSettingsDialog(settingsService, vault, vm, ExecutablesSettingsDialog.TabShortcutsIndex);
                var tabs = (TabControl)settingsDlg.FindName("SettingsTabs");
                Assert.Equal(7, tabs.Items.Count);
                Assert.Equal(ExecutablesSettingsDialog.TabShortcutsIndex, tabs.SelectedIndex);
                var shortcutsList = (ItemsControl)settingsDlg.FindName("ShortcutsItemsControl");
                Assert.Equal(11, shortcutsList.Items.Count);
                settingsDlg.Close();

                // 5. Renderiza modo claro (Light) + compacto + fonte 16 (combinação independente de fonte maior com densidade compacta)
                settingsService.Current.Theme = AppThemeMode.Light;
                settingsService.Current.FontSize = 15;
                settingsService.Current.ComfortableMode = false;
                ThemeService.ApplyTheme(AppThemeMode.Light, Application.Current!.Resources);
                ViewPreferencesService.Apply(settingsService.Current);
                vm.RefreshThemeProperties();
                if (window.Content is FrameworkElement root)
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = 1000;
                    window.Height = 680;
                    window.UpdateAdaptiveHeaderAndPaneBounds(1000);
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                    root.Measure(new Size(1000, 680));
                    root.Arrange(new Rect(0, 0, 1000, 680));
                    root.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 680, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var outDir = Path.Combine(LanguageAndDiagnosticsTests.ProjectRoot, "artifacts", "ui-comparison", "after-1.8.0");
                    Directory.CreateDirectory(outDir);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(outDir, "main-1000x680-light-compact-font15.png"));
                    encoder.Save(fs);
                }
            }
            finally
            {
                ThemeService.ApplyTheme(AppThemeMode.Dark, Application.Current!.Resources);
                ViewPreferencesService.Apply(new AppSettings());
                window.Close();
            }
        });

    public void Dispose()
    {
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch { }
    }
}
