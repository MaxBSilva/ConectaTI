using System.Windows.Controls;
using System.Windows.Input;
using ConectaTI.App.Services.Connections;

namespace ConectaTI.App.Helpers;

// Operates on ItemCollection (the displayed/sorted/filtered view), never repository indexes.
public static class ConnectionListNavigation
{
    public static void Move(ListBox list, int direction)
    {
        if (list.Items.Count == 0) return;
        var index = list.SelectedIndex;
        // Shift+arrows can leave SelectedItem at the start of a range. Continue from its
        // focused selected endpoint, but never from an old, unselected search container.
        if (Keyboard.FocusedElement is ListBoxItem focused && focused.IsSelected
            && ItemsControl.ItemsControlFromItemContainer(focused) == list)
            index = list.ItemContainerGenerator.IndexFromContainer(focused);
        var next = ConnectionListQuery.MoveIndex(list.Items.Count, index, direction);
        SelectAndFocus(list, list.Items[next]);
    }

    public static void SelectAndFocus(ListBox list, object item)
    {
        if (!list.Items.Contains(item)) return;
        var ownsFocus = list.IsKeyboardFocusWithin;
        if (list.SelectedItems.Count != 1 || !Equals(list.SelectedItem, item))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = item;
        }
        list.ScrollIntoView(item);
        // Materialize virtualized/recycled containers synchronously so scroll and selection
        // are always up to date; only move keyboard focus if the list already owned focus.
        list.UpdateLayout();
        if (!ownsFocus) return;
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();
    }
}
