using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ConectaTI.App.Data;
using ConectaTI.App.Editing;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;

namespace ConectaTI.App.Views;

public partial class CredentialsDialog : Window
{
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;
    private readonly ITextClipboard _clipboardService;
    private readonly SettingsService? _settingsService;
    private SavedCredential? _currentEditing;
    private readonly PasswordRevealLease _passwordReveal = new();
    private long _generation;
    private long _revision;
    private bool _closed;
    private bool _saving;

    public CredentialsDialog(
        SqliteRepository repository,
        CredentialVaultService vaultService,
        ITextClipboard? clipboardService = null,
        SettingsService? settingsService = null)
    {
        InitializeComponent();
        LocalizedWindow.Attach(this);
        _repository = repository;
        _vaultService = vaultService;
        _settingsService = settingsService;
        _clipboardService = clipboardService ?? new ClipboardService(new AppLogger(new AppPaths()));
        Loaded += async (_, _) => await ReloadListAsync();
        Closed += (_, _) => { _closed = true; InvalidateReveal(); };
        foreach (var box in new[] { CredNameBox, CredUserBox, CredDomainBox, CredNotesBox })
            box.TextChanged += (_, _) => EditorChanged();
        CredPasswordBox.PasswordChanged += (_, _) => EditorChanged();
    }

    private void InvalidateReveal()
    {
        _passwordReveal.Cancel();
        _generation++;
        RevealedSecretBox.Text = L.Text("Senha salva: ••••••••••••");
        StatusText.Text = string.Empty;
    }

    private void EditorChanged()
    {
        _revision++;
        InvalidateReveal();
    }

    private Func<bool> CaptureCurrent()
    {
        var guard = new EditorOperationGuard(() => new EditorOperationState(
            _closed ? null : _currentEditing, _currentEditing?.Id, _currentEditing?.Id,
            _generation, _revision, _revision));
        return guard.IsCurrent;
    }

    private static bool HasStoredSecret(SavedCredential? cred)
        => cred is not null &&
           (cred.SecretStorage == CredentialSecretStorage.LegacyWindows ||
            !string.IsNullOrWhiteSpace(cred.ProtectedSecretBase64));

    public static string FormatCredentialAccessInfo(
        string? name,
        string? username,
        string? domain,
        string? password,
        string? notes)
    {
        var sb = new StringBuilder();
        var cleanName = (name ?? string.Empty).Trim();
        var effectiveUser = UserIdentityFormatter.FormatDisplay(username ?? string.Empty, domain ?? string.Empty);
        var cleanNotes = (notes ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(cleanName))
            sb.AppendLine($"{L.Text("Credencial")}: {cleanName}");
        if (!string.IsNullOrWhiteSpace(effectiveUser))
            sb.AppendLine($"{L.Text("Usuário")}: {effectiveUser}");
        if (!string.IsNullOrEmpty(password))
            sb.AppendLine($"{L.Text("Senha")}: {password}");
        if (!string.IsNullOrWhiteSpace(cleanNotes))
            sb.AppendLine($"{L.Text("Observações")}: {cleanNotes}");

        return sb.ToString().TrimEnd();
    }

    private int GetEffectiveClipboardClearSeconds()
        => Math.Clamp(_settingsService?.Current.ClipboardClearSeconds ?? 30, 5, 300);

    private async Task<bool> ReloadListAsync(string? selectId = null)
    {
        var generation = _generation;
        var list = await _repository.GetAllCredentialsAsync();
        if (_closed || generation != _generation) return false;
        CredentialsListBox.ItemsSource = list;
        if (!string.IsNullOrWhiteSpace(selectId))
        {
            CredentialsListBox.SelectedItem = list.FirstOrDefault(c => c.Id == selectId);
        }
        else if (list.Count > 0 && CredentialsListBox.SelectedItem == null)
        {
            CredentialsListBox.SelectedIndex = 0;
        }
        else if (list.Count == 0)
        {
            StartNewCredential();
        }
        return true;
    }

    private void StartNewCredential()
    {
        InvalidateReveal();
        _currentEditing = new SavedCredential { Id = Guid.NewGuid().ToString("D") };
        CredNameBox.Text = string.Empty;
        CredUserBox.Text = string.Empty;
        CredDomainBox.Text = string.Empty;
        CredPasswordBox.Clear();
        CredNotesBox.Text = string.Empty;
        RevealedSecretBox.Text = L.Text("Nova credencial (informe a senha acima)");
        StorageText.Text = _currentEditing.StorageLocationDisplay;
        CredNameBox.Focus();
    }

    private void NewCredential_Click(object sender, RoutedEventArgs e)
    {
        CredentialsListBox.SelectedItem = null;
        StartNewCredential();
    }

    private void CredentialsListBox_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CredentialsListBox, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            if (!item.IsSelected)
                CredentialsListBox.SelectedItem = item.DataContext;
        }
    }

    private void CredentialsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateReveal();
        if (CredentialsListBox.SelectedItem is SavedCredential cred)
        {
            _currentEditing = cred;
            CredNameBox.Text = cred.Name;
            CredUserBox.Text = cred.Username;
            CredDomainBox.Text = cred.Domain;
            CredPasswordBox.Clear();
            CredNotesBox.Text = cred.Notes;
            StorageText.Text = cred.StorageLocationDisplay;
            RevealedSecretBox.Text = _vaultService.AvailabilityMessage(cred) ??
                (cred.SecretStorage != CredentialSecretStorage.LegacyWindows && string.IsNullOrWhiteSpace(cred.ProtectedSecretBase64)
                    ? L.Text("Senha salva: (nenhuma)") : L.Text("Senha salva: ••••••••••••"));
        }
    }

    private async void CopyCredUser_Click(object sender, RoutedEventArgs e)
        => await CopySelectedCredentialUserAsync();

    internal async Task<bool> CopySelectedCredentialUserAsync()
    {
        if (_closed) return false;
        var current = CaptureCurrent();
        var userDisplay = UserIdentityFormatter.FormatDisplay(CredUserBox.Text, CredDomainBox.Text);
        if (string.IsNullOrWhiteSpace(userDisplay))
        {
            StatusText.Text = L.Text("Nenhum usuário informado nesta credencial.");
            return false;
        }

        var ok = await _clipboardService.CopyTextWithRetryAsync(userDisplay, isSecret: false, autoClearSeconds: 0, canCopy: current);
        if (!current()) return false;
        StatusText.Text = ok
            ? L.Text("Usuário copiado.")
            : L.Text("Não foi possível acessar a área de transferência.");
        return ok;
    }

    private async void CopyCredPassword_Click(object sender, RoutedEventArgs e)
        => await CopySelectedCredentialPasswordAsync();

    internal async Task<bool> CopySelectedCredentialPasswordAsync()
    {
        if (_closed) return false;
        var current = CaptureCurrent();
        var target = _currentEditing;
        var typedPassword = CredPasswordBox.Password;

        string? secret = null;
        if (!string.IsNullOrEmpty(typedPassword))
        {
            secret = typedPassword;
        }
        else if (HasStoredSecret(target))
        {
            try
            {
                secret = await _vaultService.ReadSecretAsync(target!);
            }
            catch (Exception ex)
            {
                if (!current()) return false;
                _vaultService.RecordCredentialFailure(ex);
                StatusText.Text = CredentialVaultService.RecoveryMessage(ex);
                return false;
            }
        }

        if (!current()) return false;

        if (string.IsNullOrEmpty(secret))
        {
            StatusText.Text = L.Text("Esta credencial não possui senha armazenada.");
            return false;
        }

        var clearSec = GetEffectiveClipboardClearSeconds();
        var ok = await _clipboardService.CopyTextWithRetryAsync(
            secret,
            isSecret: true,
            autoClearSeconds: clearSec,
            canCopy: current);
        if (!current()) return false;

        StatusText.Text = ok
            ? L.Format("Senha copiada (limpeza automática em {0}s).", clearSec)
            : L.Text("Não foi possível acessar a área de transferência.");
        return ok;
    }

    private async void CopyCredFullAccess_Click(object sender, RoutedEventArgs e)
        => await CopySelectedCredentialAccessAsync();

    internal async Task<bool> CopySelectedCredentialAccessAsync()
    {
        if (_closed) return false;
        var current = CaptureCurrent();
        var target = _currentEditing;
        var name = CredNameBox.Text;
        var rawUser = CredUserBox.Text;
        var rawDomain = CredDomainBox.Text;
        var typedPassword = CredPasswordBox.Password;
        var notes = CredNotesBox.Text;

        string? secret = null;
        if (!string.IsNullOrEmpty(typedPassword))
        {
            secret = typedPassword;
        }
        else if (HasStoredSecret(target))
        {
            try
            {
                secret = await _vaultService.ReadSecretAsync(target!);
            }
            catch (Exception ex)
            {
                if (!current()) return false;
                _vaultService.RecordCredentialFailure(ex);
                StatusText.Text = CredentialVaultService.RecoveryMessage(ex);
                return false;
            }
        }

        if (!current()) return false;

        var formatted = FormatCredentialAccessInfo(name, rawUser, rawDomain, secret, notes);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            StatusText.Text = L.Text("Nenhuma informação preenchida para copiar.");
            return false;
        }

        var hasSecret = !string.IsNullOrEmpty(secret);
        var clearSec = GetEffectiveClipboardClearSeconds();
        var ok = await _clipboardService.CopyTextWithRetryAsync(
            formatted,
            isSecret: hasSecret,
            autoClearSeconds: hasSecret ? clearSec : 0,
            canCopy: current);
        if (!current()) return false;

        if (!ok)
        {
            StatusText.Text = L.Text("Não foi possível acessar a área de transferência.");
            return false;
        }

        StatusText.Text = hasSecret
            ? L.Format("Dados de acesso copiados (limpeza automática em {0}s).", clearSec)
            : L.Text("Dados de acesso copiados.");
        return true;
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
        => await SaveSelectedCredentialAsync();

    internal async Task SaveSelectedCredentialAsync()
    {
        if (_saving || _closed) return;
        if (_currentEditing == null)
            _currentEditing = new SavedCredential();

        var name = CredNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, L.Text("Informe um nome de identificação para a credencial."), L.Text("Validação"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var norm = UserIdentityFormatter.Normalize(CredUserBox.Text, CredDomainBox.Text);
        var target = System.Text.Json.JsonSerializer.Deserialize<SavedCredential>(
            System.Text.Json.JsonSerializer.Serialize(_currentEditing))!;
        target.Name = name;
        target.Username = norm.IsUpn ? norm.RawUsername : norm.CleanUsername;
        target.Domain = norm.IsUpn ? string.Empty : norm.Domain;
        target.Notes = CredNotesBox.Text.Trim();

        var replacement = CredPasswordBox.Password;
        var current = CaptureCurrent();
        _saving = true;
        try
        {
            await _vaultService.SaveApplicationCredentialAsync(target, replacement);
            if (!current()) return;
            CredPasswordBox.Clear();
            current = CaptureCurrent();
            if (await ReloadListAsync(target.Id))
                StatusText.Text = L.Format("Credencial '{0}' salva com sucesso.", target.Name);
        }
        catch (Exception ex)
        {
            if (!current()) return;
            _vaultService.RecordCredentialFailure(ex);
            StatusText.Text = CredentialVaultService.RecoveryMessage(ex);
        }
        finally { _saving = false; }
    }

    private async void DeleteCredential_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditing == null || CredentialsListBox.SelectedItem == null)
            return;

        var res = MessageBox.Show(
            this,
            L.Format("Deseja realmente excluir a credencial '{0}'?\nConexões vinculadas a ela passarão a ficar sem credencial salva.", (_currentEditing.Name)),
            L.Text("Confirmar Exclusão"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes)
            return;

        var current = CaptureCurrent();
        await _vaultService.DeleteApplicationCredentialAsync(_currentEditing.Id);
        if (!current()) return;
        StatusText.Text = L.Text("Credencial excluída.");
        await ReloadListAsync();
    }

    private async void RevealCredPassword_Click(object sender, RoutedEventArgs e)
        => await RevealSelectedPasswordAsync();

    internal async Task RevealSelectedPasswordAsync()
    {
        if (_currentEditing == null || !HasStoredSecret(_currentEditing))
        {
            StatusText.Text = L.Text("Esta credencial não possui senha armazenada.");
            return;
        }

        var target = _currentEditing;
        var current = _passwordReveal.BeginRequest(CaptureCurrent());

        try
        {
            var plain = await _vaultService.ReadSecretAsync(target);
            if (!current()) return;
            StorageText.Text = target.StorageLocationDisplay;
            RevealedSecretBox.Text = L.Text("Senha:") + " " + plain;
            StatusText.Text = L.Text("Senha revelada por 10 segundos.");
            await _passwordReveal.ScheduleAutoHideAsync(TimeSpan.FromSeconds(10), token =>
            {
                if (current() && !token.IsCancellationRequested)
                {
                    RevealedSecretBox.Text = L.Text("Senha salva: ••••••••••••");
                    StatusText.Text = L.Text("Senha ocultada novamente.");
                }
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!current()) return;
            MessageBox.Show(this, CredentialVaultService.RecoveryMessage(ex), L.Text("Erro ao Revelar Senha"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
