using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        var originalLanguage = LocalizationService.Current.SelectedLanguage;
        var output = Path.Combine(LanguageAndDiagnosticsTests.ProjectRoot, "artifacts", "copy-responsive", "ui");
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
                foreach (var scale in new[] { 1d, 1.25, 1.5 })
                {
                    vm.SettingsService.Current.FontSize = scale == 1.5 ? 18 : 12.5;
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
                    var stacked = width < 680;
                    Assert.True(stacked ? listBounds.Bottom <= detailsBounds.Top : listBounds.Right <= detailsBounds.Left);
                    Assert.True(listBounds.Height > 60 && detailsBounds.Height > 80);
                    Assert.True(detailsBounds.Right <= width + 1 && detailsBounds.Bottom <= height + 1);
                    Assert.True(viewport.ViewportWidth > 260);
                    Assert.True(viewport.ExtentWidth <= viewport.ViewportWidth + 1);
                    copy.BringIntoView();
                    await Flush();
                    var copyBounds = Bounds(copy, viewport);
                    Assert.True(copyBounds.Top >= -1 && copyBounds.Bottom <= viewport.ActualHeight + 1,
                        $"Complete copy button inaccessible at {width}/{language}: {copyBounds}, viewport={viewport.ActualHeight}");
                    Assert.True(copyBounds.Right <= viewport.ActualWidth + 1);
                    observations.Add(new { language = language.ToString(), width, height, renderDpi = 96 * scale, stacked,
                        list = listBounds.ToString(), details = detailsBounds.ToString(), copy = copyBounds.ToString() });
                    var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"main-{language}-{width:0}x{height:0}-{scale * 100:0}.png"));
                    encoder.Save(file);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(new
            {
                observations,
                note = "WPF renders at 96/120/144 DPI; Windows display scale settings were not changed."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            root.Width = originalWidth;
            root.Height = originalHeight;
            vm.SettingsService.Current.FontSize = originalFont;
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
