using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ConectaTI.App.Models;

namespace ConectaTI.App.Security;

public sealed class CredentialUserContextException() : CryptographicException(
    "Credential belongs to another Windows user.");
public sealed class CredentialMigrationRequiredException() : CryptographicException(
    "Credential requires migration from the previous master-password format.");
internal sealed record AutomaticSecretEnvelope(int Version, string Scope, string Owner, string Blob, string Checksum);

public partial class CredentialVaultService
{
    internal const string AutomaticPrefix = "CTDPAPI2:";
    // Public context metadata is not a key. DPAPI alone supplies the secret protection.
    protected virtual string CurrentUserContext
    {
        get
        {
            using var identity=WindowsIdentity.GetCurrent();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User?.Value
                ?? throw new InvalidOperationException("Windows identity unavailable."))));
        }
    }

    private static byte[] AutomaticEntropy(string id, string owner) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { "ConectaTI.CurrentUser", "2", id, owner });
    private static string EnvelopeChecksum(string owner, string blob) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("ConectaTI.CurrentUser|2|CurrentUser|" + owner + "|" + blob)));

    private string ProtectAutomaticSecret(string id, string plain)
    {
        var owner = CurrentUserContext;
        var bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            var blob = Convert.ToBase64String(ProtectedData.Protect(bytes, AutomaticEntropy(id,owner),
                DataProtectionScope.CurrentUser));
            var envelope = new AutomaticSecretEnvelope(2, "CurrentUser", owner, blob, EnvelopeChecksum(owner,blob));
            return AutomaticPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private string UnprotectAutomaticSecret(string id, string text)
    {
        AutomaticSecretEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AutomaticSecretEnvelope>(
                Convert.FromBase64String(text[AutomaticPrefix.Length..])) ?? throw new CryptographicException("Missing DPAPI envelope.");
            if (envelope.Version != 2 || envelope.Scope != "CurrentUser" || envelope.Owner.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Checksum),
                    Convert.FromHexString(EnvelopeChecksum(envelope.Owner,envelope.Blob))))
                throw new CryptographicException("Invalid DPAPI envelope.");
            _ = Convert.FromBase64String(envelope.Blob);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NullReferenceException)
        { throw new CryptographicException("Damaged DPAPI envelope.", ex); }
        // A differing recorded SID is a distinct condition. Other DPAPI failures remain technical errors.
        // The checksum detects accidental envelope damage; DPAPI authenticates the payload plus entropy.
        if (envelope.Owner != CurrentUserContext) throw new CredentialUserContextException();
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Blob), AutomaticEntropy(id,envelope.Owner),
            DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private bool NeedsPasswordMigration(SavedCredential credential) =>
        credential.SecretStorage==CredentialSecretStorage.ApplicationVault ||
        credential.ProtectedSecretBase64.StartsWith(EnvelopePrefixMasterPlusDpapi,StringComparison.Ordinal) ||
        (credential.SecretStorage==CredentialSecretStorage.LegacyWindows && _settingsService.Current.HasMasterPassword);

    public async Task<bool> HasPreviousVaultCredentialsAsync() =>
        (await _repository.GetAllCredentialsAsync()).Any(NeedsPasswordMigration);

    public string? AvailabilityMessage(SavedCredential credential)
    {
        if(NeedsPasswordMigration(credential))
            return RecoveryMessage(new CredentialMigrationRequiredException());
        if(!credential.ProtectedSecretBase64.StartsWith(AutomaticPrefix,StringComparison.Ordinal)) return null;
        try
        {
            var envelope=JsonSerializer.Deserialize<AutomaticSecretEnvelope>(
                Convert.FromBase64String(credential.ProtectedSecretBase64[AutomaticPrefix.Length..]));
            if(envelope == null || envelope.Version!=2 || envelope.Scope!="CurrentUser" ||
                envelope.Checksum!=EnvelopeChecksum(envelope.Owner,envelope.Blob))
                return RecoveryMessage(new CryptographicException());
            return envelope.Owner==CurrentUserContext ? null : RecoveryMessage(new CredentialUserContextException());
        }
        catch(Exception ex) when(ex is JsonException or FormatException or ArgumentException)
        { return RecoveryMessage(new CryptographicException()); }
    }

    public static string RecoveryMessage(Exception exception) => exception switch
    {
        CredentialUserContextException => L.Text("Senha protegida por outro usuário. Importe um backup com senhas ou informe uma nova senha."),
        CredentialMigrationRequiredException => L.Text("Senha no formato anterior. Migre usando a senha mestra anterior ou informe uma nova senha."),
        _ => L.Text("Não foi possível recuperar a senha: dados corrompidos ou proteção do Windows indisponível. Consulte o diagnóstico ou informe uma nova senha.")
    };

    public void RecordCredentialFailure(Exception exception) => _logger.RecordFailure("Credentials.Save",exception);
    public void RecordMigrationFailure(Exception exception) => _logger.RecordFailure("Credentials.MigrateAutomatic",exception);

    public async Task<VaultMigrationReport> MigratePreviousVaultAsync(string password, CancellationToken token = default)
    {
        // Wrong password, cancellation and any read/write failure leave every original record intact.
        await _repository.CredentialOperationGate.WaitAsync(token);
        try
        {
            var candidates = (await _repository.GetAllCredentialsAsync()).Where(NeedsPasswordMigration).ToList();
            if (candidates.Count == 0) return new(0,Array.Empty<VaultMigrationIssue>());
            if (candidates.Any(c => c.SecretStorage == CredentialSecretStorage.ApplicationVault))
                await UnlockVaultAsync(password);
            if (candidates.Any(c => c.ProtectedSecretBase64.StartsWith(EnvelopePrefixMasterPlusDpapi,StringComparison.Ordinal) ||
                c.SecretStorage==CredentialSecretStorage.LegacyWindows && _settingsService.Current.HasMasterPassword) &&
                !UnlockLegacyMasterPassword(password)) throw new CryptographicException("Invalid legacy master password.");
            var expected = new Dictionary<string,string>();
            foreach (var credential in candidates)
            {
                token.ThrowIfCancellationRequested();
                var plain = await Task.Run(() => credential.SecretStorage==CredentialSecretStorage.LegacyWindows
                    ? ReadLegacySecret(credential) : UnprotectSecret(credential),token);
                if (string.IsNullOrEmpty(plain)) throw new CryptographicException("Unreadable migration source.");
                expected.Add(credential.Id,plain);
                credential.ProtectedSecretBase64 = await Task.Run(() => ProtectSecret(credential.Id,credential.EffectiveUserDisplay,plain),token);
            }
            await _repository.CommitAutomaticMigrationAsync(candidates,c =>
            {
                if (UnprotectSecret(c) != expected[c.Id]) throw new CryptographicException("Migration readback failed.");
            }, token);
            return new(candidates.Count,Array.Empty<VaultMigrationIssue>());
        }
        finally { Lock(); _repository.CredentialOperationGate.Release(); }
    }
}
