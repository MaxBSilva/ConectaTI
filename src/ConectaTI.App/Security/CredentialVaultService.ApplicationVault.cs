using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConectaTI.App.Models;

namespace ConectaTI.App.Security;

public sealed record ApplicationVaultMetadata(int FormatVersion, string VaultId, string Cipher, string Kdf,
    int Iterations, string Salt, string WrappedKey, string WrapNonce, string WrapTag);
public sealed record VaultMigrationIssue(string CredentialId, string Name, string ErrorType);
public sealed record VaultMigrationReport(int MigratedCount, IReadOnlyList<VaultMigrationIssue> Pending);
internal sealed record VaultSecretEnvelope(int Version, string VaultId, string Nonce, string Ciphertext, string Tag);

public partial class CredentialVaultService
{
    public const int VaultPbkdf2Iterations = 600_000;
    private const string VaultPrefix = "CTVAULT1:";
    private readonly object _cryptoGate = new();
    private readonly SemaphoreSlim _vaultMetadataGate = new(1,1);
    private byte[]? _dataKey;
    private ApplicationVaultMetadata? _vaultMetadata;
    private Queue<byte[]> _nonces = new();
    private long _vaultGeneration;
    public bool IsVaultCreated => _repository.ReadApplicationVaultMetadata() != null;

    // Daily protection never needs an application password. Old vault keys are used only by migration.
    public Func<Task<bool>>? RequestMigrationAsync { get; set; }
    private readonly SemaphoreSlim _migrationGate = new(1,1);

    public async Task<bool> TryMigratePreviousVaultAsync(CancellationToken token = default)
    {
        await _migrationGate.WaitAsync(token);
        try
        {
            if (!await HasPreviousVaultCredentialsAsync()) return true;
            return RequestMigrationAsync != null && await RequestMigrationAsync();
        }
        finally { _migrationGate.Release(); }
    }

    private static ApplicationVaultMetadata ParseMetadata(string json)
    {
        var metadata = JsonSerializer.Deserialize<ApplicationVaultMetadata>(json)
            ?? throw new CryptographicException("Invalid vault metadata.");
        if (metadata.FormatVersion != 1 || !Guid.TryParse(metadata.VaultId, out _) ||
            metadata.Cipher != "AES-256-GCM" || metadata.Kdf != "PBKDF2-HMAC-SHA256" ||
            metadata.Iterations < VaultPbkdf2Iterations || metadata.Iterations > 5_000_000 ||
            Convert.FromBase64String(metadata.Salt).Length < 16)
            throw new CryptographicException("Unsupported or damaged vault metadata.");
        return metadata;
    }

    private static byte[] KeyAad(ApplicationVaultMetadata metadata) => JsonSerializer.SerializeToUtf8Bytes(new[]
    { "ConectaTI.VaultKey", metadata.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        metadata.VaultId, metadata.Cipher, metadata.Kdf, metadata.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.Salt });
    private static byte[] SecretAad(string vaultId, string credentialId) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { "ConectaTI.Secret", "1", vaultId, credentialId });

    private static byte[] DeriveVaultKey(string password, ApplicationVaultMetadata metadata)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, Convert.FromBase64String(metadata.Salt),
            metadata.Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static ApplicationVaultMetadata WrapDataKey(string password, byte[] dataKey, string vaultId)
    {
        var metadata = new ApplicationVaultMetadata(1, vaultId, "AES-256-GCM", "PBKDF2-HMAC-SHA256",
            VaultPbkdf2Iterations, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "", "", "");
        var wrappingKey = DeriveVaultKey(password, metadata);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[32]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Encrypt(nonce, dataKey, cipher, tag, KeyAad(metadata));
            return metadata with { WrappedKey=Convert.ToBase64String(cipher), WrapNonce=Convert.ToBase64String(nonce),
                WrapTag=Convert.ToBase64String(tag) };
        }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    private static byte[] UnwrapDataKey(string password, ApplicationVaultMetadata metadata)
    {
        var wrappingKey = DeriveVaultKey(password, metadata);
        var dataKey = new byte[32];
        try
        {
            using var aes = new AesGcm(wrappingKey,16);
            var cipher = Convert.FromBase64String(metadata.WrappedKey);
            if (cipher.Length != 32) throw new CryptographicException("Invalid wrapped data key.");
            aes.Decrypt(Convert.FromBase64String(metadata.WrapNonce), cipher, Convert.FromBase64String(metadata.WrapTag),
                dataKey, KeyAad(metadata));
            return dataKey;
        }
        catch { CryptographicOperations.ZeroMemory(dataKey); throw; }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    private static void ValidateNewPassword(string password, string confirmation)
    {
        if (password.Length < 8) throw new ArgumentException(L.Text("Use uma senha mestra com pelo menos 8 caracteres."));
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            throw new ArgumentException(L.Text("A confirmação da senha mestra não confere."));
    }

    public async Task CreateVaultAsync(string password, string confirmation)
    {
        ValidateNewPassword(password,confirmation);
        await _vaultMetadataGate.WaitAsync();
        byte[]? key = null;
        var generation = Interlocked.Read(ref _vaultGeneration);
        try
        {
            if (await Task.Run(() => IsVaultCreated)) throw new InvalidOperationException(L.Text("O cofre já existe."));
            key = RandomNumberGenerator.GetBytes(32);
            var metadata = await Task.Run(() => WrapDataKey(password,key,Guid.NewGuid().ToString("D")));
            await Task.Run(() => _repository.StoreApplicationVaultMetadata(JsonSerializer.Serialize(metadata), null));
            lock (_cryptoGate)
            {
                if (generation != _vaultGeneration) throw new OperationCanceledException();
                _dataKey = key; key = null; _vaultMetadata = metadata;
            }
            LockStateChanged?.Invoke(this,EventArgs.Empty);
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); _vaultMetadataGate.Release(); }
    }

    public async Task UnlockVaultAsync(string password)
    {
        await _vaultMetadataGate.WaitAsync();
        byte[]? key = null;
        var generation = Interlocked.Read(ref _vaultGeneration);
        try
        {
            var json = await Task.Run(_repository.ReadApplicationVaultMetadata)
                ?? throw new InvalidOperationException(L.Text("Crie o cofre antes de desbloquear."));
            var metadata = ParseMetadata(json);
            key = await Task.Run(() => UnwrapDataKey(password,metadata));
            lock (_cryptoGate)
            {
                if (generation != _vaultGeneration) throw new OperationCanceledException();
                if (_dataKey != null) CryptographicOperations.ZeroMemory(_dataKey);
                _dataKey=key; key=null; _vaultMetadata=metadata; _nonces.Clear();
            }
            LockStateChanged?.Invoke(this,EventArgs.Empty);
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); _vaultMetadataGate.Release(); }
    }

    public async Task ChangeVaultPasswordAsync(string currentPassword, string newPassword, string confirmation)
    {
        ValidateNewPassword(newPassword,confirmation);
        await _vaultMetadataGate.WaitAsync();
        byte[]? key = null;
        try
        {
            var original = await Task.Run(_repository.ReadApplicationVaultMetadata)
                ?? throw new InvalidOperationException(L.Text("Crie o cofre antes de desbloquear."));
            var metadata = ParseMetadata(original);
            key = await Task.Run(() => UnwrapDataKey(currentPassword,metadata));
            var updated = await Task.Run(() => WrapDataKey(newPassword,key,metadata.VaultId));
            await Task.Run(() => _repository.StoreApplicationVaultMetadata(JsonSerializer.Serialize(updated),original));
            lock (_cryptoGate) { if (_dataKey != null) _vaultMetadata=updated; }
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); _vaultMetadataGate.Release(); }
    }

    internal string EncryptApplicationSecret(string credentialId, string plain)
    {
        lock (_cryptoGate)
        {
            if (_dataKey == null || _vaultMetadata == null) throw new InvalidOperationException(L.Text("Desbloqueie o cofre para continuar."));
            if (_nonces.Count == 0) _nonces = _repository.ReserveApplicationVaultNonces(_vaultMetadata.VaultId);
            var nonce = _nonces.Dequeue();
            var bytes = Encoding.UTF8.GetBytes(plain); var cipher = new byte[bytes.Length]; var tag = new byte[16];
            try
            {
                using var aes = new AesGcm(_dataKey,16);
                aes.Encrypt(nonce,bytes,cipher,tag,SecretAad(_vaultMetadata.VaultId,credentialId));
                var envelope = new VaultSecretEnvelope(1,_vaultMetadata.VaultId,Convert.ToBase64String(nonce),
                    Convert.ToBase64String(cipher),Convert.ToBase64String(tag));
                return VaultPrefix+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private string DecryptApplicationSecret(string credentialId, string envelopeString)
    {
        lock (_cryptoGate)
        {
            if (_dataKey == null || _vaultMetadata == null) throw new InvalidOperationException(L.Text("Desbloqueie o cofre para continuar."));
            if (!envelopeString.StartsWith(VaultPrefix,StringComparison.Ordinal)) throw new CryptographicException("Invalid vault secret format.");
            var envelope = JsonSerializer.Deserialize<VaultSecretEnvelope>(Convert.FromBase64String(envelopeString[VaultPrefix.Length..]))
                ?? throw new CryptographicException("Invalid vault secret format.");
            if (envelope.Version != 1 || envelope.VaultId != _vaultMetadata.VaultId) throw new CryptographicException("Vault identity mismatch.");
            var cipher = Convert.FromBase64String(envelope.Ciphertext);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(_dataKey,16);
                aes.Decrypt(Convert.FromBase64String(envelope.Nonce),cipher,Convert.FromBase64String(envelope.Tag),
                    plain,SecretAad(envelope.VaultId,credentialId));
                return Encoding.UTF8.GetString(plain);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
}
