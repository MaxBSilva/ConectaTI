using System.Windows;
using System.Windows.Controls;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

namespace ConectaTI.App.Views;

public partial class CredentialsDialog : Window
{
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;
    private SavedCredential? _currentEditing;
    private CancellationTokenSource? _revealCts;

    public CredentialsDialog(SqliteRepository repository, CredentialVaultService vaultService)
    {
        InitializeComponent();
        _repository = repository;
        _vaultService = vaultService;
        Loaded += async (_, _) => await ReloadListAsync();
        Closed += (_, _) => _revealCts?.Cancel();
    }

    private async Task ReloadListAsync(string? selectId = null)
    {
        var list = await _repository.GetAllCredentialsAsync();
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
    }

    private void StartNewCredential()
    {
        _revealCts?.Cancel();
        _currentEditing = new SavedCredential { Id = Guid.NewGuid().ToString("D") };
        CredNameBox.Text = string.Empty;
        CredUserBox.Text = string.Empty;
        CredDomainBox.Text = string.Empty;
        CredPasswordBox.Clear();
        CredNotesBox.Text = string.Empty;
        RevealedSecretBox.Text = "Nova credencial (informe a senha acima)";
        CredNameBox.Focus();
    }

    private void NewCredential_Click(object sender, RoutedEventArgs e)
    {
        CredentialsListBox.SelectedItem = null;
        StartNewCredential();
    }

    private void CredentialsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _revealCts?.Cancel();
        if (CredentialsListBox.SelectedItem is SavedCredential cred)
        {
            _currentEditing = cred;
            CredNameBox.Text = cred.Name;
            CredUserBox.Text = cred.Username;
            CredDomainBox.Text = cred.Domain;
            CredPasswordBox.Clear();
            CredNotesBox.Text = cred.Notes;
            RevealedSecretBox.Text = string.IsNullOrWhiteSpace(cred.ProtectedSecretBase64)
                ? "Senha salva: (nenhuma)"
                : "Senha salva: ••••••••••••";
        }
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultService.IsLocked)
        {
            MessageBox.Show(this, "O cofre está bloqueado.", "Cofre Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_currentEditing == null)
            _currentEditing = new SavedCredential();

        var name = CredNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Informe um nome de identificação para a credencial.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var norm = UserIdentityFormatter.Normalize(CredUserBox.Text, CredDomainBox.Text);
        _currentEditing.Name = name;
        _currentEditing.Username = norm.IsUpn ? norm.RawUsername : norm.CleanUsername;
        _currentEditing.Domain = norm.IsUpn ? string.Empty : norm.Domain;
        _currentEditing.Notes = CredNotesBox.Text.Trim();

        if (!string.IsNullOrEmpty(CredPasswordBox.Password))
        {
            _currentEditing.ProtectedSecretBase64 = _vaultService.ProtectSecret(
                _currentEditing.Id,
                _currentEditing.EffectiveUserDisplay,
                CredPasswordBox.Password);
            CredPasswordBox.Clear();
        }

        await _repository.SaveCredentialAsync(_currentEditing);
        StatusText.Text = $"Credencial '{_currentEditing.Name}' salva com sucesso.";
        await ReloadListAsync(_currentEditing.Id);
    }

    private async void DeleteCredential_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditing == null || CredentialsListBox.SelectedItem == null)
            return;

        var res = MessageBox.Show(
            this,
            $"Deseja realmente excluir a credencial '{_currentEditing.Name}'?\nConexões vinculadas a ela passarão a ficar sem credencial salva.",
            "Confirmar Exclusão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes)
            return;

        _vaultService.RemoveFromWindowsVault(_currentEditing.Id);
        await _repository.DeleteCredentialAsync(_currentEditing.Id);
        StatusText.Text = "Credencial excluída.";
        await ReloadListAsync();
    }

    private async void RevealCredPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditing == null || string.IsNullOrWhiteSpace(_currentEditing.ProtectedSecretBase64))
        {
            StatusText.Text = "Esta credencial não possui senha armazenada.";
            return;
        }

        if (_vaultService.IsLocked)
        {
            StatusText.Text = "Desbloqueie o aplicativo para revelar senhas.";
            return;
        }

        _revealCts?.Cancel();
        var cts = new CancellationTokenSource();
        _revealCts = cts;

        try
        {
            var plain = _vaultService.UnprotectSecret(_currentEditing);
            RevealedSecretBox.Text = $"Senha: {plain}";
            StatusText.Text = "Senha revelada por 10 segundos.";
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                RevealedSecretBox.Text = "Senha salva: ••••••••••••";
                StatusText.Text = "Senha ocultada novamente.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao Revelar Senha", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
