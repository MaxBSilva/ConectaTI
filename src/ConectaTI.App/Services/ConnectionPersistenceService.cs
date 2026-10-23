using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

namespace ConectaTI.App.Services;

public record ConnectionSaveResult(ConnectionEntry Entry, bool CleanupPending);

// Coordinates SQLite and Windows: prepare a dedicated credential, commit the link,
// then retire unreferenced secrets. Cleanup survives failures and process termination.
public sealed class ConnectionPersistenceService(
    SqliteRepository repository, CredentialVaultService vault, AppLogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ConnectionSaveResult> SaveAsync(ConnectionEntry snapshot,
        PasswordEditIntent intent, string password, string credentialName)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = snapshot.Clone();
            SavedCredential? staged = null;
            if (intent == PasswordEditIntent.Replace)
            {
                if (password.Length == 0) throw new InvalidOperationException("A nova senha está vazia.");
                staged = new SavedCredential
                {
                    Name = string.IsNullOrWhiteSpace(credentialName) ? $"{entry.ClientName} - {entry.Name}" : credentialName.Trim(),
                    Username = entry.Username,
                    Domain = entry.Domain
                };
                staged.ProtectedSecretBase64 = await Task.Run(() => vault.ProtectSecret(
                    staged.Id, staged.EffectiveUserDisplay, password, writeToWindowsVault: false));
                // A crash before the link commit leaves a durable cleanup instruction.
                await repository.QueueVaultCleanupAsync(staged.Id);
                try { await Task.Run(() => vault.WriteToWindowsVault(staged)); }
                catch { await CleanupCoreAsync(); throw; }
                entry.CredentialId = staged.Id;
            }
            else if (intent == PasswordEditIntent.Remove) entry.CredentialId = null;

            try { await repository.SaveConnectionWithCredentialAsync(entry, staged); }
            catch { await CleanupCoreAsync(); throw; }
            var clean = await CleanupCoreAsync();
            return new ConnectionSaveResult(entry, !clean);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RecoverVaultCleanupAsync()
    {
        await _gate.WaitAsync();
        try { return await CleanupCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<bool> CleanupCoreAsync()
    {
        try
        {
            foreach (var id in await repository.GetPendingVaultCleanupAsync())
            {
                await Task.Run(() => vault.RemoveFromWindowsVault(id));
                await repository.CompleteVaultCleanupAsync(id);
            }
            return true;
        }
        catch (Exception ex)
        {
            // Never log exception messages or payloads from vault operations.
            logger.Warn("VaultCleanup", $"Limpeza pendente: {ex.GetType().Name}");
            return false;
        }
    }
}
