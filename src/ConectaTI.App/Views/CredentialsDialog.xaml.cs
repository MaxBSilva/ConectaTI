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
        LocalizedWindow.Attach(this);
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
        RevealedSecretBox.Text = L.Text("Nova credencial (informe a senha acima)");
        StorageText.Text = _currentEditing.StorageLocationDisplay;
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
            StorageText.Text = cred.StorageLocationDisplay;
            RevealedSecretBox.Text = _vaultService.AvailabilityMessage(cred) ??
                (cred.SecretStorage != CredentialSecretStorage.LegacyWindows && string.IsNullOrWhiteSpace(cred.ProtectedSecretBase64)
                    ? L.Text("Senha salva: (nenhuma)") : L.Text("Senha salva: ••••••••••••"));
        }
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditing == null)
            _currentEditing = new SavedCredential();

        var name = CredNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, L.Text("Informe um nome de identificação para a credencial."), L.Text("Validação"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var norm = UserIdentityFormatter.Normalize(CredUserBox.Text, CredDomainBox.Text);
        _currentEditing.Name = name;
        _currentEditing.Username = norm.IsUpn ? norm.RawUsername : norm.CleanUsername;
        _currentEditing.Domain = norm.IsUpn ? string.Empty : norm.Domain;
        _currentEditing.Notes = CredNotesBox.Text.Trim();

        var replacement=CredPasswordBox.Password;
        var target=_currentEditing;
        try
        {
            await _vaultService.SaveApplicationCredentialAsync(target,replacement);
            if(_currentEditing?.Id==target.Id && CredPasswordBox.Password==replacement)CredPasswordBox.Clear();
            StatusText.Text=L.Format("Credencial '{0}' salva com sucesso.",target.Name);
            await ReloadListAsync(target.Id);
        }
        catch(Exception ex)
        {
            _vaultService.RecordCredentialFailure(ex);
            StatusText.Text=CredentialVaultService.RecoveryMessage(ex);
        }
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

        await _vaultService.DeleteApplicationCredentialAsync(_currentEditing.Id);
        StatusText.Text = L.Text("Credencial excluída.");
        await ReloadListAsync();
    }

    private async void RevealCredPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditing == null || (_currentEditing.SecretStorage != CredentialSecretStorage.LegacyWindows && string.IsNullOrWhiteSpace(_currentEditing.ProtectedSecretBase64)))
        {
            StatusText.Text = L.Text("Esta credencial não possui senha armazenada.");
            return;
        }

        _revealCts?.Cancel();
        var cts = new CancellationTokenSource();
        _revealCts = cts;

        try
        {
            var plain = await _vaultService.ReadSecretAsync(_currentEditing);
            StorageText.Text = _currentEditing.StorageLocationDisplay;
            RevealedSecretBox.Text = L.Text("Senha:") + " " + plain;
            StatusText.Text = L.Text("Senha revelada por 10 segundos.");
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                RevealedSecretBox.Text = L.Text("Senha salva: ••••••••••••");
                StatusText.Text = L.Text("Senha ocultada novamente.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, CredentialVaultService.RecoveryMessage(ex), L.Text("Erro ao Revelar Senha"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
