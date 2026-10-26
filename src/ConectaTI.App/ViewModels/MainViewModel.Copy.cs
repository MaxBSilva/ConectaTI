using CommunityToolkit.Mvvm.Input;
using ConectaTI.App.Models;

namespace ConectaTI.App.ViewModels;

public partial class MainViewModel
{
    private enum AccessCopyField { Name, Address, AddressWithPort, User, Password, Complete }

    private sealed record AccessCopySnapshot(ConnectionEditSession Session, long Generation, long Revision,
        long PasswordRevision, string Name, string Address, string User, string? CredentialId,
        string Password, PasswordEditIntent PasswordIntent);

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    public Task CopyCompleteAccessAsync() => SelectedConnection is null
        ? Task.CompletedTask : CopyAccessFieldAsync(AccessCopyField.Complete);

    private AccessCopySnapshot? CaptureAccessCopy()
    {
        var session = _activeSession;
        if (session is null) return null;
        return new(session, _editorGeneration, session.Revision, session.PasswordRevision,
            EditingEntry.Name, InlineHostAndPort, InlineUserWithDomain, EditingEntry.CredentialId,
            InlinePasswordInput, session.PasswordIntent);
    }

    private bool IsCopyCurrent(AccessCopySnapshot copy) => ReferenceEquals(copy.Session, _activeSession)
        && copy.Generation == _editorGeneration && copy.Revision == copy.Session.Revision
        && copy.PasswordRevision == copy.Session.PasswordRevision
        && copy.Name == EditingEntry.Name && copy.Address == InlineHostAndPort
        && copy.User == InlineUserWithDomain && copy.Password == InlinePasswordInput;

    private async Task CopyAccessFieldAsync(AccessCopyField field)
    {
        // Os quatro campos pertencem à mesma sessão/revisão, antes de qualquer await.
        var copy = CaptureAccessCopy();
        if (copy is null) return;
        if (field == AccessCopyField.Complete && copy.Session.Entry.Id != SelectedConnection?.Id) return;

        var user = copy.User;
        var password = copy.PasswordIntent == PasswordEditIntent.Remove ? string.Empty : copy.Password;
        var needsUser = field is AccessCopyField.User or AccessCopyField.Complete;
        var needsPassword = field is AccessCopyField.Password or AccessCopyField.Complete;
        var readSavedPassword = needsPassword && password.Length == 0 && copy.PasswordIntent != PasswordEditIntent.Remove;

        if (((needsUser && string.IsNullOrWhiteSpace(user)) || readSavedPassword)
            && !string.IsNullOrWhiteSpace(copy.CredentialId))
        {
            try
            {
                var credential = await _repository.GetCredentialByIdAsync(copy.CredentialId);
                if (!IsCopyCurrent(copy)) return;
                if (credential is null)
                {
                    copy.Session.CredentialRecoveryFailed = true;
                    StatusMessage = L.Text("Falha ao recuperar credencial");
                    NotifyPasswordStatusProperties();
                    return;
                }
                if (needsUser && string.IsNullOrWhiteSpace(user)) user = credential.EffectiveUserDisplay;
                if (readSavedPassword)
                {
                    var recovered = await RecoverPasswordAsync(credential, copy.Session);
                    if (!IsCopyCurrent(copy) || recovered is null) return;
                    password = recovered;
                }
            }
            catch (Exception exception)
            {
                // Nunca registrar conteúdo do acesso ou da área de transferência.
                _logger.Warn("Clipboard.Credentials", exception.GetType().Name);
                if (IsCopyCurrent(copy)) StatusMessage = L.Text("Falha ao recuperar credencial");
                return;
            }
        }

        if (!IsCopyCurrent(copy)) return;
        var text = field switch
        {
            AccessCopyField.Name => copy.Name,
            AccessCopyField.Address or AccessCopyField.AddressWithPort => copy.Address,
            AccessCopyField.User => user,
            AccessCopyField.Password => password,
            // Não passar o conteúdo com senha por L.Format, que mantém cache de mensagens.
            _ => $"Nome: {copy.Name}{System.Environment.NewLine}Endereço: {copy.Address}{System.Environment.NewLine}Usuário: {user}{System.Environment.NewLine}Senha: {password}"
        };
        if (string.IsNullOrEmpty(text))
        {
            if (field == AccessCopyField.Password) StatusMessage = L.Text("Nenhuma senha cadastrada.");
            return;
        }

        var clearSeconds = _settingsService.Current.ClipboardClearSeconds > 0
            ? _settingsService.Current.ClipboardClearSeconds : 30;
        var copied = await _clipboardService.CopyTextWithRetryAsync(text,
            isSecret: needsPassword, autoClearSeconds: needsPassword ? clearSeconds : 0,
            canCopy: () => IsCopyCurrent(copy));
        if (!IsCopyCurrent(copy)) return;
        StatusMessage = copied ? L.Text(field switch
        {
            AccessCopyField.Name => "Nome da conexão copiado",
            AccessCopyField.Address => "Endereço copiado",
            AccessCopyField.AddressWithPort => "Host:porta copiado",
            AccessCopyField.User => "Usuário copiado",
            AccessCopyField.Password => "Senha copiada",
            _ => "Dados do acesso copiados"
        }) : L.Text("Não foi possível copiar para a área de transferência.");
    }
}
