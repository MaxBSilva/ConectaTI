using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

namespace ConectaTI.App.Services;

public record ConnectionSaveResult(ConnectionEntry Entry, bool CleanupPending);

// App secrets and links commit together in SQLite. Windows cleanup only retires old app-owned copies.
public sealed class ConnectionPersistenceService(
    SqliteRepository repository, CredentialVaultService vault, AppLogger logger)
{
    private SemaphoreSlim Gate => repository.CredentialOperationGate;

    public async Task<ConnectionSaveResult> SaveAsync(ConnectionEntry snapshot,
        PasswordEditIntent intent, string password, string credentialName)
    {
        await Gate.WaitAsync();
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
                entry.CredentialId = staged.Id;
            }
            else if (intent == PasswordEditIntent.Remove) entry.CredentialId = null;

            try { await repository.SaveConnectionWithCredentialAsync(entry, staged); }
            catch { await CleanupCoreAsync(); throw; }
            var clean = await CleanupCoreAsync();
            return new ConnectionSaveResult(entry, !clean);
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> RecoverVaultCleanupAsync()
    {
        await Gate.WaitAsync();
        try { return await CleanupCoreAsync(); }
        finally { Gate.Release(); }
    }

    internal Task<bool> CleanupWithGateHeldAsync() => CleanupCoreAsync();

    private Task<bool> CleanupCoreAsync()
    {
        // Kept for compatibility with old callers/queues. Windows originals are never deleted automatically.
        logger.Info("Vault", "App secrets committed in SQLite; Windows originals preserved.");
        return Task.FromResult(true);
    }
}
