using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ConectaTI.App.Models;
using ConectaTI.App.ViewModels;

namespace ConectaTI.Tests;

internal static class WpfEditorScenarios
{
    public static async Task VerifyAsync(ConectaTI.App.MainWindow window, MainViewModel vm)
    {
        vm.ConfirmUnsavedChangesCallback = _ => throw new Exception("Unexpected save prompt");
        var list = (ListBox)window.FindName("ConnectionsListBox");
        var button = (Button)window.FindName("SaveConnectionButton");
        var name = (TextBox)window.FindName("ConnectionNameTextBox");
        var password = (PasswordBox)window.FindName("InlinePasswordBox");
        var a = vm.SelectedConnection!;
        var b = vm.FilteredConnections.First(c => c.Id != a.Id);
        var count = vm.FilteredConnections.Count;

        async Task FlushAsync() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

        await FlushAsync();
        Assert.False(button.IsEnabled);
        var originalName = vm.EditingEntry.Name;
        name.Text = "Alterado pela interface";
        await FlushAsync();
        Assert.Equal("Alterado pela interface", vm.EditingEntry.Name);
        Assert.True(button.IsEnabled);
        Assert.Equal("Salvar alterações", ((TextBlock)button.Content).Text);
        Assert.Equal("#FFF59E0B", button.Background.ToString());
        name.Text = originalName;
        await FlushAsync();
        Assert.False(button.IsEnabled);

        vm.InlinePasswordInput = "rascunho fictício";
        await FlushAsync();
        Assert.Equal("rascunho fictício", password.Password);
        list.SelectedItem = b;
        await FlushAsync();
        Assert.Equal(b.Id, vm.EditingEntry.Id);
        Assert.Empty(password.Password);
        list.SelectedItem = a;
        await FlushAsync();
        Assert.Equal("rascunho fictício", password.Password);
        vm.CancelEdit();

        for (var i = 0; i < 8; i++)
        {
            vm.EditingEntry.Notes = $"UI edit {i}";
            Assert.True(await vm.SaveConnectionAsync());
            await FlushAsync();
            Assert.Equal(count, list.Items.Count);
            Assert.Equal(vm.SelectedConnection?.Id, (list.SelectedItem as ConnectionEntry)?.Id);
            var target = vm.FilteredConnections.First(c => c.Id != vm.SelectedConnection!.Id);
            list.SelectedItem = target;
            await FlushAsync();
            Assert.Equal(target.Id, vm.EditingEntry.Id);
            Assert.Equal(vm.EditingEntry.Name, name.Text);
        }
        vm.EditingEntry.Notes = "Alteração pendente para inspeção visual";
        await FlushAsync();
        Assert.True(button.IsEnabled);
        Assert.Contains("Alterações não salvas", vm.PendingChangesText);
    }
}
