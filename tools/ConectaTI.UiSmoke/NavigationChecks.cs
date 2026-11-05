using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.ViewModels;
namespace ConectaTI.UiSmoke;

internal static class NavigationChecks
{
    public static async Task RunAsync(ConectaTI.App.MainWindow window, MainViewModel vm)
    {
        var list = (ListBox)window.FindName("ConnectionsListBox");
        var search = (TextBox)window.FindName("SearchTextBox");
        async Task Flush() => await window.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Select(int index)
        {
            list.SelectedItem = list.Items[index]; list.ScrollIntoView(list.SelectedItem); list.UpdateLayout();
            ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).Focus();
        }
        void KeyPress(Key key, bool expectedHandled = true)
        {
            var source = Keyboard.FocusedElement as UIElement ?? list;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(args);
            if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; source.RaiseEvent(args); }
            if (expectedHandled) Check(args.Handled, $"{key} was not handled once");
        }
        void CheckCurrent(int index)
        {
            Check(list.SelectedIndex == index, $"Expected displayed index {index}, got {list.SelectedIndex}");
            Check(vm.SelectedConnection?.Id == ((ConnectionEntry)list.Items[index]).Id, "SelectedItem / VM mismatch");
            Check(vm.EditingEntry.Id == vm.SelectedConnection!.Id, "Details mismatch");
            Check(Keyboard.FocusedElement is ListBoxItem item && item.DataContext == list.SelectedItem, "Keyboard focus is stale");
        }
        window.Activate(); await Flush();
        Select(10);
        foreach (var key in new[] { Key.A, Key.V, Key.I, Key.C, Key.O, Key.L, Key.A }) KeyPress(key);
        CheckCurrent(60); // Must fail before the fix: focus remains at the old item.
        KeyPress(Key.Down); CheckCurrent(61); KeyPress(Key.Up); CheckCurrent(60);
        await Task.Delay(1200); KeyPress(Key.Down); CheckCurrent(61); await Flush(); CheckCurrent(61);
        Select(150); KeyPress(Key.Down); CheckCurrent(151); KeyPress(Key.Up); CheckCurrent(150);
        Select(0); KeyPress(Key.Up); CheckCurrent(0);
        Select(list.Items.Count - 1); KeyPress(Key.Down); CheckCurrent(list.Items.Count - 1);
        // Filtered view has gaps in the repository order, while navigation must advance just one visible row.
        await vm.SetViewModeAsync(ConnectionViewMode.Favorites); await Flush(); Select(5);
        foreach (var key in new[] { Key.A, Key.V, Key.I, Key.C, Key.O, Key.L, Key.A }) KeyPress(key);
        var filteredIndex = list.SelectedIndex;
        Check(vm.SelectedConnection!.Name == "Avícola 060", "Accent-insensitive search failed");
        KeyPress(Key.Down); CheckCurrent(filteredIndex + 1);
        Check(vm.SelectedConnection!.Name == "Avícola 062", "Used unfiltered index");
        KeyPress(Key.Up); CheckCurrent(filteredIndex);
        // Form/Search focus must not be stolen by programmatic selection/focus synchronization.
        search.Focus(); ConnectionListNavigation.SelectAndFocus(list, list.Items[0]);
        Check(Keyboard.FocusedElement == search, "Stole editor focus");
        vm.SearchText = "Avícola"; await Flush();
        Select(0); KeyPress(Key.Down); CheckCurrent(1);
        Check(vm.SelectedConnection!.Name == "Avícola 062", "Search field filter order lost");
        Check(list.SelectionMode == SelectionMode.Extended, "Multiple selection disabled");
        list.SelectedItems.Add(list.Items[2]); Check(list.SelectedItems.Count == 2, "Multiple selection broken");
        list.ScrollIntoView(list.Items[2]); list.UpdateLayout();
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2)).Focus();
        KeyPress(Key.Up); CheckCurrent(1);
        Check(!list.IsTextSearchEnabled, "Native type search still competes with custom search");

        // Validate Todos / Ativos / Inativos activity filter + avicola search + QuickTexts button presence
        var allBtn = (Button)window.FindName("ActivityAllFilterButton");
        var activeBtn = (Button)window.FindName("ActivityActiveFilterButton");
        var inactiveBtn = (Button)window.FindName("ActivityInactiveFilterButton");
        var quickTextsBtn = (Button)window.FindName("QuickTextsButton");
        Check(allBtn != null && activeBtn != null && inactiveBtn != null && quickTextsBtn != null, "Missing new header/filter controls");

        await vm.SetViewModeAsync(ConnectionViewMode.All);
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.All);
        vm.SearchText = "avicola";
        await Flush();
        if (vm.FilteredConnections.Count(c => !c.IsActive) < 2)
        {
            foreach (var target in vm.FilteredConnections.Take(2).ToList())
            {
                vm.SelectedConnection = target;
                vm.EditingEntry.IsActive = false;
                Check(await vm.SaveConnectionAsync(), "Failed to save inactive status in navigation check");
            }
        }
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Inactive);
        await Flush();
        Check(vm.FilteredConnections.Count >= 2 && vm.FilteredConnections.All(c => !c.IsActive), "Avícola + Inativos filter returned active items");
        Select(0);
        KeyPress(Key.Down);
        CheckCurrent(1);
        KeyPress(Key.Up);
        CheckCurrent(0);

        // Restore active state on modified items so subsequent runs start clean
        foreach (var target in vm.FilteredConnections.Where(c => c.Id is "nav-060" or "nav-061").ToList())
        {
            vm.SelectedConnection = target;
            vm.EditingEntry.IsActive = true;
            await vm.SaveConnectionAsync();
        }
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.All);
        vm.SearchText = string.Empty;
        await Flush();
    }
}

