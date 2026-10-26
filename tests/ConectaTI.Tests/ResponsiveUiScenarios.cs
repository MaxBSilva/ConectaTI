using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.ViewModels;

namespace ConectaTI.Tests;

internal static class ResponsiveUiScenarios
{
    public static async Task VerifyAsync(ConectaTI.App.MainWindow window, MainViewModel vm)
    {
        var root = (FrameworkElement)window.Content;
        var originalWidth = root.Width;
        var originalHeight = root.Height;
        var originalFont = vm.SettingsService.Current.FontSize;
        var originalDensity = vm.SettingsService.Current.Density;
        var originalLanguage = LocalizationService.Current.SelectedLanguage;
        var output = Path.Combine(LanguageAndDiagnosticsTests.ProjectRoot, "artifacts", "side-by-side", "ui");
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        try
        {
            Assert.Equal("ConectaTI", window.Title);
            Assert.NotNull(window.Icon);
            var header = (WrapPanel)window.FindName("HeaderActions");
            Assert.DoesNotContain(header.Children.OfType<Image>(), _ => true);
            var copy = (Button)window.FindName("CopyCompleteAccessButton");
            Assert.Equal(vm.HasSelectedConnection, copy.IsEnabled);
            var viewport = (ScrollViewer)window.FindName("DetailsScrollViewer");
            foreach (var language in new[] { AppLanguage.Portuguese, AppLanguage.English, AppLanguage.Spanish })
            {
                LocalizationService.Current.SetLanguage(language);
                Assert.DoesNotContain("CopyCompleteAccess", ((TextBlock)copy.Content).Text);
                foreach (var (width, height) in new[] { (1440d, 900d), (900d, 650d), (480d, 380d) })
                foreach (var fontSize in ViewPreferencesService.FontSizePresets)
                foreach (var density in Enum.GetValues<InterfaceDensity>())
                {
                    var scale = density == InterfaceDensity.Compact ? 1d : density == InterfaceDensity.Standard ? 1.25 : 1.5;
                    vm.SettingsService.Current.FontSize = fontSize;
                    vm.SettingsService.Current.Density = density;
                    ViewPreferencesService.Apply(vm.SettingsService.Current);
                    root.Width = width;
                    root.Height = height;
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    await Flush();
                    foreach (var command in header.Children.OfType<FrameworkElement>())
                    {
                        var bounds = Bounds(command, header);
                        Assert.True(bounds.Left >= -1 && bounds.Right <= header.ActualWidth + 1,
                            $"Header command outside width at {width}/{language}: {bounds}");
                    }
                    var list = (FrameworkElement)window.FindName("AccessListPane");
                    var details = (FrameworkElement)window.FindName("AccessDetailsPane");
                    var listBounds = Bounds(list, root);
                    var detailsBounds = Bounds(details, root);
                    Assert.Equal(0, Grid.GetColumn(list)); Assert.Equal(2, Grid.GetColumn(details));
                    Assert.Equal(Grid.GetRow(list), Grid.GetRow(details));
                    Assert.True(listBounds.Right <= detailsBounds.Left);
                    Assert.Equal(listBounds.Top, detailsBounds.Top);
                    Assert.True(listBounds.Width >= 130 && detailsBounds.Width >= 300);
                    Assert.True(listBounds.Height > 60 && detailsBounds.Height > 80);
                    Assert.True(((ListBox)window.FindName("ConnectionsListBox")).ActualHeight >= fontSize * 1.4);
                    Assert.True(detailsBounds.Right <= width + 1 && detailsBounds.Bottom <= height + 1);
                    Assert.True(viewport.ViewportWidth > 240);
                    Assert.True(viewport.ExtentWidth <= viewport.ViewportWidth + 1);
                    copy.BringIntoView();
                    await Flush();
                    var copyBounds = Bounds(copy, viewport);
                    Assert.True(copyBounds.Top >= -1 && copyBounds.Bottom <= viewport.ActualHeight + 1,
                        $"Complete copy button inaccessible at {width}/{language}: {copyBounds}, viewport={viewport.ActualHeight}");
                    Assert.True(copyBounds.Right <= viewport.ActualWidth + 1);
                    Assert.Equal(fontSize, vm.SettingsService.Current.FontSize);
                    Assert.Equal(fontSize, window.FontSize);
                    var nameBox = (TextBox)window.FindName("ConnectionNameTextBox");
                    Assert.True(nameBox.ActualHeight >= nameBox.FontSize + nameBox.Padding.Top + nameBox.Padding.Bottom);
                    observations.Add(new { language = language.ToString(), width, height, renderDpi = 96 * scale, fontSize, density = density.ToString(),
                        list = listBounds.ToString(), details = detailsBounds.ToString(), copy = copyBounds.ToString() });
                    var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"main-{language}-{width:0}x{height:0}-font{fontSize:0.#}-{density}.png"));
                    encoder.Save(file);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(new
            {
                observations,
                note = "WPF renders at 96/120/144 DPI; Windows display scale settings were not changed."
            }, new JsonSerializerOptions { WriteIndented = true }));

            // A proporção escolhida no divisor acompanha a janela e é gravada para reabertura.
            root.Width = 900; root.Height = 650;
            root.Measure(new Size(900, 650)); root.Arrange(new Rect(0, 0, 900, 650)); await Flush();
            var columns = (Grid)window.FindName("MainWorkspace");
            var left = (ColumnDefinition)window.FindName("LeftColumnDef");
            left.Width = new GridLength((columns.ActualWidth - 4) * .33);
            await Flush();
            ((GridSplitter)window.FindName("PaneSplitter")).RaiseEvent(new DragCompletedEventArgs(0, 0, false)
            { RoutedEvent = Thumb.DragCompletedEvent });
            root.Width = 1440;
            root.Measure(new Size(1440, 650)); root.Arrange(new Rect(0, 0, 1440, 650)); await Flush();
            Assert.InRange(left.ActualWidth / (columns.ActualWidth - 4), .328, .332);
            typeof(ConectaTI.App.MainWindow).GetMethod("SaveWindowGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var reopened = new SettingsService(vm.Paths, new AppLogger(vm.Paths)).Load();
            Assert.InRange(reopened.LeftPaneRatio, .328, .332);
            Assert.Equal(vm.SettingsService.Current.FontSize, reopened.FontSize);
            Assert.Equal(vm.SettingsService.Current.Density, reopened.Density);
        }
        finally
        {
            root.Width = originalWidth;
            root.Height = originalHeight;
            vm.SettingsService.Current.FontSize = originalFont;
            vm.SettingsService.Current.Density = originalDensity;
            ViewPreferencesService.Apply(vm.SettingsService.Current);
            LocalizationService.Current.SetLanguage(originalLanguage);
        }

        async Task Flush()
        {
            await window.Dispatcher.InvokeAsync(() => root.UpdateLayout(), DispatcherPriority.ContextIdle);
            root.UpdateLayout();
        }
    }

    private static Rect Bounds(FrameworkElement item, Visual ancestor)
        => item.TransformToAncestor(ancestor).TransformBounds(new Rect(item.RenderSize));
}
