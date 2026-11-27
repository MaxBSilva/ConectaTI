using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;

namespace ConectaTI.App.Services;

public record ConnectionSaveResult(ConnectionEntry Entry, bool CleanupPending);

// App secrets and links commit together in SQLite. Windows cleanup only retires old app-owned copies.
public sealed class ConnectionPersistenceService(
    SqliteRepository repository, CredentialVaultService vault, AppLogger logger)
{
    private SemaphoreSlim Gate => repository.CredentialOperationGate;

    public async Task<ConnectionSaveResult> SaveAsync(
        ConnectionEntry snapshot,
        PasswordEditIntent intent,
        string password,
        string credentialName,
        PasswordEditIntent vpnIntent = PasswordEditIntent.Keep,
        string vpnPassword = "")
    {
        await Gate.WaitAsync();
        try
        {
            var entry = snapshot.Clone();
            entry.RdpOptions.AdditionalProperties = RdpFileService.Sanitize(entry.RdpOptions.AdditionalProperties);
            entry.RdpOptions.OriginalContent = RdpFileService.SanitizeOriginal(entry.RdpOptions.OriginalContent);
            if (entry.Protocol == ConnectionProtocol.Rdp) RdpFileService.Build(entry);

            SavedCredential? staged = null;
            if (intent == PasswordEditIntent.Replace)
            {
                if (password.Length == 0) throw new InvalidOperationException(L.Text("A nova senha está vazia."));
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

            SavedCredential? stagedVpn = null;
            if (vpnIntent == PasswordEditIntent.Replace)
            {
                if (vpnPassword.Length == 0) throw new InvalidOperationException(L.Text("A nova senha da VPN está vazia."));
                var parsedVpnUser = UserIdentityFormatter.Normalize(entry.VpnUsername, null);
                var vpnLabel = !string.IsNullOrWhiteSpace(entry.VpnName) ? entry.VpnName : entry.Name;
                stagedVpn = new SavedCredential
                {
                    Name = $"VPN - {entry.ClientName} - {vpnLabel}".Trim(' ', '-'),
                    Username = parsedVpnUser.IsUpn ? parsedVpnUser.RawUsername : parsedVpnUser.CleanUsername,
                    Domain = parsedVpnUser.IsUpn ? string.Empty : parsedVpnUser.Domain
                };
                stagedVpn.ProtectedSecretBase64 = await Task.Run(() => vault.ProtectSecret(
                    stagedVpn.Id, stagedVpn.EffectiveUserDisplay, vpnPassword, writeToWindowsVault: false));
                entry.VpnCredentialId = stagedVpn.Id;
            }
            else if (vpnIntent == PasswordEditIntent.Remove) entry.VpnCredentialId = null;

            try
            {
                await repository.SaveConnectionWithCredentialsAsync(entry, staged, stagedVpn);
            }
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
