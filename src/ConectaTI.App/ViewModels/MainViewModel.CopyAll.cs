using CommunityToolkit.Mvvm.Input;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
namespace ConectaTI.App.ViewModels;
public partial class MainViewModel
{
    [RelayCommand]
    public async Task CopyAllDataAsync()
    {
        if (ActiveEditSession == null) return;
        CaptureSession();
        var current = CaptureEditorGuard();
        var session = ActiveEditSession;
        var entry = EditingEntry.Clone();
        var host = InlineHostAndPort;
        var user = InlineUserWithDomain;
        var password = session.PasswordIntent == PasswordEditIntent.Remove ? "" : session.Password;
        try
        {
            if (string.IsNullOrEmpty(password) && session.PasswordIntent == PasswordEditIntent.Keep && !string.IsNullOrWhiteSpace(entry.CredentialId))
            {
                var read = await _credentials.ReadAsync(entry.CredentialId, current);
                if (!current()) return;
                if (read.Status != Security.CredentialReadStatus.Available) throw new InvalidOperationException();
                password = read.Secret;
            }
            // A delayed secret read must never copy data from a previous selection or revision.
            if (!current()) return;
            var clearSeconds = _settingsService.Current.ClipboardClearSeconds > 0 ? _settingsService.Current.ClipboardClearSeconds : 30;
            if (await _credentials.CopyConnectionAsync(entry, host, user, password, current, clearSeconds))
            {
                if (current()) StatusMessage = L.Text("Dados do acesso copiados");
            }
            else if (current()) StatusMessage = L.Text("Não foi possível copiar os dados do acesso.");
        }
        catch { if (current()) StatusMessage = L.Text("Não foi possível recuperar os dados para cópia."); }
    }
}

