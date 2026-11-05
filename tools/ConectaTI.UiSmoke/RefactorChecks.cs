using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.ViewModels;

namespace ConectaTI.UiSmoke;

internal static class RefactorChecks
{
    public static async Task RunAsync(ConectaTI.App.MainWindow window, MainViewModel vm,
        SqliteRepository repository, CredentialVaultService vault, Func<string?> copied)
    {
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException(description); }
        async Task Flush() => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var list = (ListBox)window.FindName("ConnectionsListBox");
        var name = (TextBox)window.FindName("ConnectionNameTextBox");
        async Task Select(string id)
        {
            list.SelectedItem = list.Items.Cast<ConnectionEntry>().Single(c => c.Id == id);
            await Flush();
            Check(vm.SelectedConnection?.Id == id && vm.EditingEntry.Id == id, "List/editor identity mismatch.");
        }

        await Select("ui-alpha");
        vm.EditingEntry.Notes = "draft Alpha";
        vm.EditingEntry.IsActive = false;
        vm.UpdatePasswordFromUser("SYNTHETIC_UI_REFACTOR_SECRET");
        await Select("ui-beta");
        vm.EditingEntry.Notes = "draft Beta";
        Check(string.IsNullOrEmpty(vm.InlinePasswordInput) && !vm.IsPasswordRevealed, "Secret crossed the selection boundary.");
        await Select("ui-alpha");
        Check(vm.EditingEntry.Notes == "draft Alpha" && !vm.EditingEntry.IsActive, "Alpha draft lost.");
        Check((await repository.GetConnectionByIdAsync("ui-alpha"))!.IsActive, "Navigation persisted a draft.");
        await vm.CopyAllDataCommand.ExecuteAsync(null);
        Check(copied()?.Contains("draft Alpha") == true && copied()?.Contains("SYNTHETIC_UI_REFACTOR_SECRET") == true
            && copied()?.Contains("gateway.example.test") == true, "Complete copy ignored collapsed draft fields.");
        Check((await repository.GetConnectionByIdAsync("ui-alpha"))!.Notes.Length == 0, "Copy persisted the editor.");

        Check(await vm.SaveConnectionAsync(), "Explicit Alpha save failed.");
        var saved = (await repository.GetConnectionByIdAsync("ui-alpha"))!;
        Check(!saved.IsActive && saved.Notes == "draft Alpha" && saved.CredentialId != null, "Saved state mismatch.");
        var credential = (await repository.GetCredentialByIdAsync(saved.CredentialId!))!;
        Check(await vault.ReadSecretAsync(credential) == "SYNTHETIC_UI_REFACTOR_SECRET", "Saved credential changed.");
        var preview = await vm.BuildRdpPreviewAsync(saved.Clone());
        Check(preview.Contains("gatewayhostname:s:gateway.example.test") && !preview.Contains("password 51")
            && !preview.Contains("SYNTHETIC_UI_REFACTOR_SECRET"), "Safe RDP preview changed.");
        vm.EditingEntry.IsActive = true;
        Check(await vm.SaveConnectionAsync(), "Reactivation failed.");
        Check((await repository.GetConnectionByIdAsync(saved.Id))!.IsActive, "Reactivation did not persist.");
        vm.EditingEntry.IsActive = false;
        Check(await vm.SaveConnectionAsync(), "Inactivation failed.");

        await Select("ui-beta");
        Check(vm.EditingEntry.Notes == "draft Beta", "Saving Alpha replaced Beta draft.");
        Check((await repository.GetConnectionByIdAsync("ui-beta"))!.Notes.Length == 0, "Beta draft was auto-saved.");
        vm.NewConnection();
        var draftId = vm.EditingEntry.Id;
        vm.EditingEntry.Name = "New unsaved draft";
        await Select("ui-alpha");
        vm.SelectedNewDraft = vm.NewDrafts.Single(c => c.Id == draftId);
        await Flush();
        Check(vm.EditingEntry.Id == draftId && vm.EditingEntry.Name == "New unsaved draft", "New draft identity lost.");
        Check(await repository.GetConnectionByIdAsync(draftId) == null, "New draft was persisted by navigation.");
        await Select("ui-alpha");

        foreach (var size in new[] { 11d, 18d })
        {
            vm.SettingsService.Current.FontSize = size;
            ViewPreferencesService.Apply(vm.SettingsService.Current);
            await Flush(); window.UpdateLayout();
            var listOrigin = list.TranslatePoint(new Point(), window);
            var editorOrigin = name.TranslatePoint(new Point(), window);
            Check(editorOrigin.X > listOrigin.X + list.ActualWidth && list.ActualWidth >= 125,
                "Panels are not side by side.");
        }
        foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark })
        { ThemeService.ApplyTheme(theme); await Flush(); }
        ThemeService.ApplyTheme(vm.SettingsService.Current.Theme);
        vm.ClearSessionDrafts();
        Check((await repository.GetConnectionByIdAsync("ui-beta"))!.Notes.Length == 0, "Clearing drafts persisted Beta.");
    }
}
