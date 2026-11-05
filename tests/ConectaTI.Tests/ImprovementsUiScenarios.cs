using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;

namespace ConectaTI.Tests;

internal static class ImprovementsUiScenarios
{
    public static async Task VerifyAsync(ConectaTI.App.MainWindow window, MainViewModel vm)
    {
        var artifacts = Path.Combine(LanguageAndDiagnosticsTests.ProjectRoot, "artifacts", "ui-1.5");
        Directory.CreateDirectory(artifacts);
        var issues = new HashSet<string>();
        var settings = vm.SettingsService.Current;
        var pendingName = vm.EditingEntry.Name;
        var pendingNote = vm.EditingEntry.Notes;
        var selectedId = vm.SelectedConnection!.Id;
        var list = (ListBox)window.FindName("ConnectionsListBox");
        var button = (Button)window.FindName("SaveConnectionButton");
        using var preferences = new PreferenceRestore(vm);
        var dialog = new ExecutablesSettingsDialog(vm.SettingsService, vm.VaultService, vm);
        var languageCombo = (ComboBox)dialog.FindName("LanguageCombo");
        foreach (var (index, label) in new[] { (1, "Salvar alterações"), (2, "Save changes"), (3, "Guardar cambios"), (0, "") })
        {
            languageCombo.SelectedIndex = index;
            await Flush(window);
            Assert.Equal((AppLanguage)index, settings.Language);
            if (label.Length > 0) Assert.Equal(label, ((TextBlock)button.Content).Text);
            Assert.Equal(selectedId, vm.SelectedConnection!.Id); Assert.Equal(pendingName, vm.EditingEntry.Name); Assert.Equal(pendingNote, vm.EditingEntry.Notes); Assert.True(vm.HasPendingChanges);
            Assert.Equal(settings.Language, new SettingsService(vm.Paths, new AppLogger(vm.Paths)).Load().Language);
        }
        settings.Language = AppLanguage.Portuguese; LocalizationService.Current.SetLanguage(settings.Language);
        var font = (Slider)dialog.FindName("FontSizeSlider"); font.Value = 18;
        Assert.Equal(18, settings.FontSize);
        var density = (ComboBox)dialog.FindName("DensityCombo"); density.SelectedIndex = 1;
        Assert.True(settings.ComfortableMode);
        dialog.Close();

        // Daily controls stay usable without any master-password session.
        var pendingPassword=vm.InlinePasswordInput;
        vm.InlinePasswordInput="Synthetic pending automatic secret";
        vm.VaultService.Lock();
        await Flush(window);
        Assert.False(vm.IsLocked);
        Assert.True(((PasswordBox)window.FindName("InlinePasswordBox")).IsEnabled);
        Assert.Equal("Synthetic pending automatic secret",((PasswordBox)window.FindName("InlinePasswordBox")).Password);
        Assert.DoesNotContain(Descendants(window).OfType<Button>(),b=>b.Content is string text && (text=="Cofre" || text=="Bloquear cofre"));
        vm.InlinePasswordInput=pendingPassword;

        await vm.Repository.SaveConnectionsBatchAsync(Enumerable.Range(0, 1000).Select(i => new ConnectionEntry
        { Id = "ui-extra-" + i, Name = $"Fictício {i:D4}", Host = "example.test", ClientName = "Teste visual" }));
        await vm.ReloadAllDataAsync(selectedId);
        Assert.Equal(pendingNote, vm.EditingEntry.Notes);
        var selected = vm.FilteredConnections.Take(2).ToArray();
        list.SelectedItems.Clear(); foreach (var item in selected) list.SelectedItems.Add(item);
        await Flush(window); Assert.Equal(2, vm.SelectedAccessCount); Assert.True(vm.CanBatchEdit);
        Assert.Equal(SelectionMode.Extended, list.SelectionMode);

        var preview = vm.ImportExportService.BuildPreviewFromCandidates(Enumerable.Range(0, 1000).Select(i => new ConnectionEntry { Name = "Fictício " + i, Host = "example.test", ClientName = "Teste visual" }).ToArray(), [], "Synthetic");
        var import = new ImportExportDialog(vm.Paths, vm.Repository, vm.ImportExportService, vm.SettingsService);
        var grid = (DataGrid)import.FindName("PreviewDataGrid"); grid.ItemsSource = preview.Items;
        var exportPanel=(Grid)import.FindName("ExportSelectionPanel");
        var exportModel=(ExportSelectionModel)exportPanel.DataContext;
        exportModel.Reload(await vm.Repository.GetAllConnectionsAsync());
        exportModel.Clear();exportModel.Query="Fictício 01";exportModel.SelectFiltered();
        var exportIds=exportModel.SelectedIds;Assert.NotEmpty(exportIds);
        exportModel.Query="unmatched synthetic query";Assert.Equal(exportIds,exportModel.SelectedIds);
        exportModel.Query="";exportModel.ReviewOnly=true;Assert.Equal(exportIds.Length,exportModel.Filtered.Count());
        var include=(CheckBox)import.FindName("IncludePasswordsCheck");
        include.IsChecked=true;
        Assert.Equal(Visibility.Visible,((WrapPanel)import.FindName("ExportPasswordPanel")).Visibility);
        ((PasswordBox)import.FindName("TransportPasswordBox")).Password="Synthetic transfer test";
        include.IsChecked=false;
        Assert.Equal(Visibility.Collapsed,((WrapPanel)import.FindName("ExportPasswordPanel")).Visibility);
        Assert.Empty(((PasswordBox)import.FindName("TransportPasswordBox")).Password);
        var batch = new BatchEditDialog(vm, selected.Select(c => c.Id));
        var settingsDialog = new ExecutablesSettingsDialog(vm.SettingsService, vm.VaultService, vm);
        foreach (var language in new[] { AppLanguage.Portuguese, AppLanguage.English, AppLanguage.Spanish })
        {
            LocalizationService.Current.SetLanguage(language);
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
            {
                settings.FontSize = 12.5; settings.ComfortableMode = false; ViewPreferencesService.Apply(settings);
                await Render(window, $"main-{language}-{scale * 100:0}-compact", Math.Max(480, 1000 / scale), Math.Max(380, 720 / scale), scale);
                settings.FontSize = 18; settings.ComfortableMode = true; ViewPreferencesService.Apply(settings);
                await Render(window, $"main-{language}-{scale * 100:0}-large", Math.Max(480, 1000 / scale), Math.Max(380, 720 / scale), scale);
            }
            var migrationDialog=new VaultMigrationDialog(vm.VaultService);
            await Render(migrationDialog,$"migration-{language}",520,360,1);
            migrationDialog.Close();
            Assert.DoesNotContain("Text",exportModel.CountText);
            settings.FontSize = 18; settings.ComfortableMode = true; ViewPreferencesService.Apply(settings);
            await Render(window, $"main-{language}-minimum-large", 480, 480, 1);
            await Render(batch, $"batch-{language}-large", 540, 480, 1);
            for (var tab = 0; tab < 3; tab++)
            {
                ((TabControl)import.FindName("ImportTabs")).SelectedIndex = tab;
                await Render(import, $"import-{language}-tab{tab}-large", 760, 600, 1);
            }
            for (var tab = 0; tab < ((TabControl)settingsDialog.FindName("SettingsTabs")).Items.Count; tab++)
            {
                ((TabControl)settingsDialog.FindName("SettingsTabs")).SelectedIndex = tab;
                await Render(settingsDialog, $"settings-{language}-tab{tab}-large", 760, 600, 1);
            }
        }
        ((TabControl)import.FindName("ImportTabs")).SelectedIndex = 0;
        await Render(import, "preview-virtualization", 960, 680, 1);
        var generatedRows = Enumerable.Range(0, grid.Items.Count).Count(i => grid.ItemContainerGenerator.ContainerFromIndex(i) != null);
        Assert.InRange(generatedRows, 1, 100);
        var generatedListItems = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
        Assert.InRange(generatedListItems, 1, 100);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "layout-observations.json"), JsonSerializer.Serialize(new { generatedRows, previewRecords = 1000, generatedListItems, listRecords = list.Items.Count, issues = issues.Order().ToArray(), note = "Windows WPF render tests at 96/120/144 dpi; these do not change the Windows display scale setting." }, new JsonSerializerOptions { WriteIndented = true }));
        import.Close(); batch.Close(); settingsDialog.Close();
        Assert.Empty(issues);

        async Task Render(Window target, string name, double width, double height, double scale)
        {
            var root = (FrameworkElement)target.Content;
            root.Width = width - root.Margin.Left - root.Margin.Right;
            root.Height = height - root.Margin.Top - root.Margin.Bottom;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height));
            await Flush(target); root.UpdateLayout();
            if (target == import && ((TabControl)import.FindName("ImportTabs")).SelectedIndex == 0)
            {
                if (grid.Columns[5].GetCellContent(preview.Items[0]) is TextBlock protocolText) Assert.Equal(preview.Items[0].Entry.ProtocolDisplay, protocolText.Text);
                if (grid.Columns[7].GetCellContent(preview.Items[0]) is TextBlock userText) Assert.Equal(preview.Items[0].Entry.EffectiveUserDisplay, userText.Text);
            }
            if(target==import && ((TabControl)import.FindName("ImportTabs")).SelectedIndex==1)
            {
                var allCheck=Descendants(root).OfType<CheckBox>().First(c=>BindingOperations.GetBinding(c,CheckBox.IsCheckedProperty)?.Path?.Path=="ExportAll");
                Assert.Equal(L.Text("Exportar todos os acessos"),allCheck.Content);
                var evidence=Descendants(root).OfType<TextBlock>().Where(t=>t.Text.Length>0).Select(t=>new
                {t.Text,t.ActualWidth,t.ActualHeight,position=t.TransformToAncestor(root).Transform(new Point(0,0)).ToString()}).ToArray();
                await File.WriteAllTextAsync(Path.Combine(artifacts,name+"-text.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
            }
            foreach (var text in Descendants(root).OfType<TextBlock>())
            {
                if (HasCollapsedAncestor(text, root)) continue;
                if (text.ActualWidth <= 0 || text.ActualHeight <= 0 || text.Text.Length == 0 || text.TextTrimming != TextTrimming.None || text.TextWrapping != TextWrapping.NoWrap) continue;
                if (BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Source != LocalizationService.Current) continue;
                var measured = new FormattedText(text.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, scale);
                if (measured.Width > text.ActualWidth + 3) issues.Add($"{name}: {text.Text} ({text.ActualWidth:F0} < {measured.Width:F0})");
            }
            var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(artifacts, name + ".png")); encoder.Save(file);
        }
    }
    private static bool HasCollapsedAncestor(DependencyObject item, DependencyObject root)
    {
        for (DependencyObject? value = item; value != null; value = VisualTreeHelper.GetParent(value))
        { if (value is UIElement element && element.Visibility != Visibility.Visible) return true; if (value == root) break; }
        return false;
    }
    private static async Task Flush(Window window) => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class PreferenceRestore(MainViewModel vm) : IDisposable
    {
        public void Dispose()
        {
            var settings = vm.SettingsService.Current;
            settings.Language = AppLanguage.Portuguese; settings.FontSize = 12.5; settings.ComfortableMode = false;
            LocalizationService.Current.SetLanguage(settings.Language); ViewPreferencesService.Apply(settings); vm.SettingsService.Save();
        }
    }
}
