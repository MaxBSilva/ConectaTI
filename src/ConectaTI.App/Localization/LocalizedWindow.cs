using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ConectaTI.App.Localization;

public static class LocalizedWindow
{
    private static readonly ConditionalWeakTable<Window, Observer> Observers = new();
    public static void Attach(Window window) => Observers.GetValue(window, w => new Observer(w));
    private sealed class Observer : ILanguageObserver
    {
        private readonly Window _window;
        public Observer(Window window)
        {
            _window = window;
            LocalizationService.Current.Observe(this);
            window.Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                if (area.Width <= 0 || area.Height <= 0) return;
                window.MinWidth = Math.Min(window.MinWidth, Math.Max(320, area.Width - 24));
                window.MinHeight = Math.Min(window.MinHeight, Math.Max(300, area.Height - 24));
                if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width, area.Width - 24);
                if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height, area.Height - 24);
            };
        }
        public void OnLanguageChanged()
        {
            if (_window.Dispatcher.HasShutdownStarted) return;
            if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(OnLanguageChanged); return; }
            Refresh(_window, new HashSet<DependencyObject>());
        }
        private static void Refresh(DependencyObject value, HashSet<DependencyObject> visited)
        {
            if (!visited.Add(value)) return;
            if (value is TextBlock boundText) boundText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            if (value is TextBlock text && text.Name.Length > 0 && !BindingOperations.IsDataBound(text, TextBlock.TextProperty)) text.Text = L.Refresh(text.Text);
            if (value is Window window && !BindingOperations.IsDataBound(window, Window.TitleProperty)) window.Title = L.Refresh(window.Title);
            if (value is DataGrid grid) foreach (var item in grid.Items.OfType<Services.ImportExport.ImportPreviewItem>()) item.RefreshDisplayProperties();
            if (value is Visual || value is System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) Refresh(VisualTreeHelper.GetChild(value, i), visited);
            foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>()) Refresh(child, visited);
        }
    }
}
