using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConectaTI.App.Models;
using ConectaTI.App.ViewModels;

namespace ConectaTI.Tests;

internal static class KeyboardNavigationUiScenarios
{
    internal static async Task VerifyAsync(ConectaTI.App.MainWindow window, MainViewModel vm)
    {
        var root = (FrameworkElement)window.Content;
        var savedWidth = root.Width;
        var savedHeight = root.Height;
        var savedClient = vm.SelectedClientFilter;
        var savedSearch = vm.SearchText;
        var savedSort = vm.SelectedSortOption;
        var savedSelection = vm.SelectedConnection?.Id;
        var list = (ListBox)window.FindName("ConnectionsListBox");
        var now = DateTime.UtcNow;
        var entries = new[] { "Início fictício", "Avícola (Serviços 9,10,11)", "Avícola (Granja Fictícia - FortClient)", "Árvore fictícia", "Último fictício" }
            .Select((name, index) => new ConnectionEntry
            {
                Name = name, ClientName = "Teclado fictício", Host = "example.test", LastOpenedAtUtc = now.AddMinutes(-index)
            }).ToArray();
        try
        {
            await vm.Repository.SaveConnectionsBatchAsync(entries);
            await vm.ReloadAllDataAsync(savedSelection);
            vm.SearchText = string.Empty;
            vm.SelectedClientFilter = "Teclado fictício";
            vm.SelectedSortOption = ConnectionSortOption.LastOpenedDesc;
            Assert.Equal(entries.Select(e => e.Id), vm.FilteredConnections.Select(e => e.Id));
            root.Width = double.NaN;
            root.Height = double.NaN;
            window.Width = 900;
            window.Height = 650;
            window.Show();
            window.Activate();
            await Flush();
            Focus(entries[^1]);

            // Reproduz o caso: o resultado está longe do item anteriormente focado.
            typeof(ConectaTI.App.MainWindow).GetMethod("ExecuteIncrementalSearch", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, ["avicola"]);
            await Flush();
            Assert.Equal(entries[1].Id, vm.SelectedConnection?.Id);
            Assert.True(Container(entries[1]).IsKeyboardFocusWithin,
                $"Busca selecionou '{vm.SelectedConnection?.Name}', mas foco permaneceu em '{(Keyboard.FocusedElement as ListBoxItem)?.DataContext}'.");
            PressKey(Key.Down);
            AssertSelection(2);
            PressKey(Key.Up);
            AssertSelection(1);
            // Limites não voltam ao início nem selecionam fora da lista filtrada.
            PressKey(Key.Home);
            PressKey(Key.Up);
            AssertSelection(0);
            PressKey(Key.End);
            PressKey(Key.Down);
            AssertSelection(4);

            Type("avicola");
            AssertSelection(1);
            PressKey(Key.Down);
            AssertSelection(2);
            Assert.Empty(vm.IncrementalSearchStatus);
            await Task.Delay(1150);
            AssertSelection(2);
            Type("arvore");
            AssertSelection(3);
            PressKey(Key.Escape);
            Type("avícola");
            AssertSelection(1);

            // Nova âncora após seleção/foco por mouse (mesmo efeito do item clicado).
            Focus(entries[3]);
            PressKey(Key.Down);
            AssertSelection(4);
            PressKey(Key.Up);
            AssertSelection(3);
            Assert.Equal("Teclado fictício", vm.SelectedClientFilter);
            Assert.Empty(vm.SearchText);
            Assert.Equal(SelectionMode.Extended, list.SelectionMode);
            list.SelectAll();
            Assert.Equal(5, list.SelectedItems.Count);
            list.SelectedItems.Clear();
            Focus(entries[1]);
            AssertSelection(1);

            var nameBox = (TextBox)window.FindName("ConnectionNameTextBox");
            nameBox.Focus();
            var before = vm.SelectedConnection?.Id;
            var textInput = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, nameBox, "outro"))
            { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
            nameBox.RaiseEvent(textInput);
            Assert.False(textInput.Handled);
            Assert.Equal(before, vm.SelectedConnection?.Id);
        }
        finally
        {
            window.Hide();
            root.Width = savedWidth;
            root.Height = savedHeight;
            vm.SelectedClientFilter = savedClient;
            vm.SearchText = savedSearch;
            vm.SelectedSortOption = savedSort;
            vm.SelectedConnection = vm.FilteredConnections.FirstOrDefault(e => e.Id == savedSelection);
        }

        ListBoxItem Container(ConnectionEntry entry)
        {
            var displayed = vm.FilteredConnections.Single(e => e.Id == entry.Id);
            list.ScrollIntoView(displayed);
            list.UpdateLayout();
            return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(displayed);
        }
        void Focus(ConnectionEntry entry)
        {
            var displayed = vm.FilteredConnections.Single(e => e.Id == entry.Id);
            list.SelectedItem = displayed;
            Assert.True(Container(entry).Focus());
        }
        void AssertSelection(int index)
        {
            Assert.Equal(entries[index].Id, vm.SelectedConnection?.Id);
            Assert.Equal(entries[index].Id, vm.EditingEntry.Id);
            Assert.True(Container(entries[index]).IsKeyboardFocusWithin);
        }
        void PressKey(Key key)
        {
            var source = (UIElement?)Keyboard.FocusedElement ?? list;
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(input);
            if (!input.Handled)
                source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                { RoutedEvent = Keyboard.KeyDownEvent });
            list.UpdateLayout();
        }
        void Type(string text)
        {
            foreach (var character in text)
            {
                var source = (UIElement?)Keyboard.FocusedElement ?? list;
                source.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, source, character.ToString()))
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            }
        }
        async Task Flush() => await window.Dispatcher.InvokeAsync(() => root.UpdateLayout(), DispatcherPriority.ContextIdle);
    }
}
