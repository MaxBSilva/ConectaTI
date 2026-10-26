using System.Security.Cryptography;
using ConectaTI.App.Models;

namespace ConectaTI.App.Security;

public partial class CredentialVaultService
{
    protected virtual string? ReadLegacyWindowsEnvelope(string id) =>
        WindowsCredentialVaultInterop.ReadGenericSecret(GetWindowsVaultTargetName(id));

    private string ReadLegacySecret(SavedCredential credential)
    {
        var envelope = credential.ProtectedSecretBase64;
        if (credential.SecretStorage == CredentialSecretStorage.LegacyWindows)
        {
            if (!_useWindowsVault) throw new CryptographicException("Legacy Windows storage unavailable.");
            envelope = ReadLegacyWindowsEnvelope(credential.Id)
                ?? throw new CryptographicException("Legacy Windows credential not found.");
        }
        if (string.IsNullOrEmpty(envelope)) throw new CryptographicException("Legacy credential is empty or unreadable.");
        return UnprotectEnvelopeString(envelope);
    }

    internal string ReadSecretForTransfer(SavedCredential credential)
    {
        if(NeedsPasswordMigration(credential))
            throw new CredentialMigrationRequiredException();
        return credential.SecretStorage==CredentialSecretStorage.LegacyWindows ? ReadLegacySecret(credential) : UnprotectSecret(credential);
    }

    public async Task<string> ReadSecretAsync(SavedCredential snapshot)
    {
        var before = await _repository.GetCredentialByIdAsync(snapshot.Id);
        if (before!=null && NeedsPasswordMigration(before))
            if (!await TryMigratePreviousVaultAsync()) throw new CredentialMigrationRequiredException();
        await _repository.CredentialOperationGate.WaitAsync();
        try
        {
            var current = await _repository.GetCredentialByIdAsync(snapshot.Id);
            if (current == null) return string.Empty;
            if (NeedsPasswordMigration(current))
                throw new CredentialMigrationRequiredException();
            return await Task.Run(() => current.SecretStorage == CredentialSecretStorage.LegacyWindows
                ? ReadLegacySecret(current) : UnprotectSecret(current));
        }
        finally { _repository.CredentialOperationGate.Release(); }
    }

    public async Task<VaultMigrationReport> MigrateLegacyCredentialsAsync(string? legacyMasterPassword = null,
        CancellationToken token = default)
    {
        if (_settingsService.Current.HasMasterPassword && !string.IsNullOrEmpty(legacyMasterPassword))
            if (!UnlockLegacyMasterPassword(legacyMasterPassword)) throw new CryptographicException("Invalid legacy master password.");
        await _repository.CredentialOperationGate.WaitAsync(token);
        try
        {
            var legacy = (await _repository.GetAllCredentialsAsync())
                .Where(c => c.SecretStorage != CredentialSecretStorage.ApplicationVault && !c.ProtectedSecretBase64.StartsWith(AutomaticPrefix,StringComparison.Ordinal)).ToList();
            var prepared = new List<SavedCredential>();
            var expected = new Dictionary<string,string>();
            var pending = new List<VaultMigrationIssue>();
            foreach (var credential in legacy)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var plain = await Task.Run(() => ReadLegacySecret(credential),token);
                    if (string.IsNullOrEmpty(plain)) throw new CryptographicException("Legacy credential unreadable.");
                    var encrypted = await Task.Run(() => ProtectSecret(credential.Id,credential.EffectiveUserDisplay,plain),token);
                    expected.Add(credential.Id,plain);
                    credential.ProtectedSecretBase64 = encrypted;
                    prepared.Add(credential);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    pending.Add(new(credential.Id,credential.Name,exception.GetType().Name));
                    _logger.RecordFailure("Vault.Migrate.Credential",exception,count:legacy.Count,recordIndex:legacy.IndexOf(credential)+1);
                }
            }
            await _repository.CommitAutomaticMigrationAsync(prepared, credential =>
            {
                if (UnprotectSecret(credential) != expected[credential.Id])
                    throw new CryptographicException("Credential migration readback failed.");
            },token);
            return new(prepared.Count,pending);
        }
        finally { _repository.CredentialOperationGate.Release(); }
    }

    public async Task SaveApplicationCredentialAsync(SavedCredential credential, string? replacementPassword = null)
    {

        await _repository.CredentialOperationGate.WaitAsync();
        try
        {
            var current = await _repository.GetCredentialByIdAsync(credential.Id);
            if (!string.IsNullOrEmpty(replacementPassword))
            {
                credential.ProtectedSecretBase64 = await Task.Run(() =>
                    ProtectSecret(credential.Id,credential.EffectiveUserDisplay,replacementPassword));
                credential.SecretStorage = CredentialSecretStorage.SqliteDpapi;
                if (current != null)
                {
                    credential.LegacyProtectedSecretBase64 = current.LegacyProtectedSecretBase64;
                    credential.LegacySecretStorage = current.LegacySecretStorage;
                }
            }
            else if (current != null)
            {
                credential.ProtectedSecretBase64=current.ProtectedSecretBase64;
                credential.SecretStorage=current.SecretStorage;
                credential.LegacyProtectedSecretBase64=current.LegacyProtectedSecretBase64;
                credential.LegacySecretStorage=current.LegacySecretStorage;
            }
            await _repository.SaveCredentialAsync(credential);
        }
        finally { _repository.CredentialOperationGate.Release(); }
    }

    public async Task DeleteApplicationCredentialAsync(string id)
    {
        await _repository.CredentialOperationGate.WaitAsync();
        try { await _repository.DeleteCredentialAsync(id); }
        finally { _repository.CredentialOperationGate.Release(); }
    }
}
