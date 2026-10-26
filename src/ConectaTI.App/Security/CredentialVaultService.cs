using System.Security.Cryptography;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Services;

namespace ConectaTI.App.Security;

/// <summary>
/// Automatic CurrentUser DPAPI secrets in SQLite. The master-password format is retained for migration.
/// DPAPI and Windows Credential Manager are retained only for reading legacy credentials.
/// Portable encrypted packages use their independent transport password.
/// </summary>
public partial class CredentialVaultService
{
    private static readonly byte[] ApplicationDpapiEntropy =
        Encoding.UTF8.GetBytes("ConectaTI::DPAPI::CurrentUser::Entropy::v1");

    private static readonly byte[] VerifierPlaintext =
        Encoding.UTF8.GetBytes("ConectaTI::MasterKeyVerifier::OK::v1");

    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSizeBytes = 32;
    private const int KeySizeBytes = 32; // 256 bits
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private const string EnvelopePrefixDpapiOnly = "DPAPI1:";
    private const string EnvelopePrefixMasterPlusDpapi = "MKGCM1:";

    private readonly SettingsService _settingsService;
    private readonly SqliteRepository _repository;
    private readonly AppLogger _logger;
    private readonly bool _useWindowsVault;

    private byte[]? _activeMasterKey;

    public bool IsLocked => false;
    internal bool IsLegacyVaultLocked { get { lock (_cryptoGate) return _dataKey == null; } }

    public event EventHandler? LockStateChanged;

    public CredentialVaultService(
        SettingsService settingsService,
        SqliteRepository repository,
        AppLogger logger,
        bool useWindowsVault = true)
    {
        _settingsService = settingsService;
        _repository = repository;
        _logger = logger;
        _useWindowsVault = useWindowsVault;
    }

    public bool RequiresMasterPassword => false;

    public void Lock()
    {
        if (_activeMasterKey != null)
        {
            CryptographicOperations.ZeroMemory(_activeMasterKey);
            _activeMasterKey = null;
        }

        lock (_cryptoGate)
        {
            Interlocked.Increment(ref _vaultGeneration);
            if (_dataKey != null) CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey=null; _vaultMetadata=null; _nonces.Clear();
        }
        LockStateChanged?.Invoke(this,EventArgs.Empty);
    }

    public bool UnlockLegacyMasterPassword(string? masterPassword = null)
    {
        if (!_settingsService.Current.HasMasterPassword)
        {
            return true;
        }

        if (string.IsNullOrEmpty(masterPassword))
            return false;

        byte[]? candidateKey=null;
        byte[]? decryptedVerifier=null;
        try
        {
            var salt = Convert.FromBase64String(_settingsService.Current.MasterPasswordSaltBase64);
            candidateKey = DeriveKeyPbkdf2(masterPassword, salt);
            var verifierBytes = Convert.FromBase64String(_settingsService.Current.MasterPasswordVerifierBase64);
            decryptedVerifier = DecryptAesGcm(verifierBytes, candidateKey);

            if (!CryptographicOperations.FixedTimeEquals(decryptedVerifier, VerifierPlaintext))
            {
                CryptographicOperations.ZeroMemory(candidateKey);
                _logger.Warn("Security", "Tentativa de validação com senha mestra incorreta.");
                return false;
            }

            if (_activeMasterKey != null)
                CryptographicOperations.ZeroMemory(_activeMasterKey);

            _activeMasterKey = candidateKey;
            candidateKey=null;
            _logger.Info("Security", "Chave mestra carregada com sucesso.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("Security", $"Falha ao validar senha mestra: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            if(candidateKey!=null)CryptographicOperations.ZeroMemory(candidateKey);
            if(decryptedVerifier!=null)CryptographicOperations.ZeroMemory(decryptedVerifier);
        }
    }

    public async Task<bool> MigrateFromMasterPasswordAsync(string previousMasterPassword)
    {
        await MigratePreviousVaultAsync(previousMasterPassword);
        return true;
    }

    /// <summary>
    /// Protege o segredo automaticamente com DPAPI CurrentUser. O parâmetro legado writeToWindowsVault não cria cópias Windows.
    /// </summary>
    public string ProtectSecret(string credentialId, string effectiveUsername, string plainPassword, bool writeToWindowsVault = false)
    {
        if (string.IsNullOrEmpty(plainPassword)) return string.Empty;
        return ProtectAutomaticSecret(credentialId,plainPassword);
    }

    /// <summary>
    /// Desprotege o segredo de uma credencial salva.
    /// </summary>
    public string UnprotectSecret(SavedCredential credential)
    {
        if (credential.SecretStorage == CredentialSecretStorage.ApplicationVault)
            return string.IsNullOrEmpty(credential.ProtectedSecretBase64) ? string.Empty
                : DecryptApplicationSecret(credential.Id,credential.ProtectedSecretBase64);
        if (credential.ProtectedSecretBase64.StartsWith(AutomaticPrefix, StringComparison.Ordinal))
            return UnprotectAutomaticSecret(credential.Id, credential.ProtectedSecretBase64);
        var envelope = credential.ProtectedSecretBase64;

        if (string.IsNullOrWhiteSpace(envelope))
            throw new CryptographicException("Legacy credential is unreadable.");

        return UnprotectEnvelopeString(envelope);
    }

    public string UnprotectEnvelopeString(string envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
            return string.Empty;


        var isMasterEncrypted = envelope.StartsWith(EnvelopePrefixMasterPlusDpapi, StringComparison.Ordinal);
        var base64Part = envelope;
        if (isMasterEncrypted)
        {
            base64Part = envelope[EnvelopePrefixMasterPlusDpapi.Length..];
        }
        else if (envelope.StartsWith(EnvelopePrefixDpapiOnly, StringComparison.Ordinal))
        {
            base64Part = envelope[EnvelopePrefixDpapiOnly.Length..];
        }

        var dpapiBlob = Convert.FromBase64String(base64Part);
        var innerPayload = ProtectedData.Unprotect(
            dpapiBlob,
            ApplicationDpapiEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            if (isMasterEncrypted)
            {
                if (_activeMasterKey == null)
                    throw new InvalidOperationException(L.Text("A chave da senha mestra não está carregada na sessão."));

                var plainBytes = DecryptAesGcm(innerPayload, _activeMasterKey);
                try
                {
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }

            return Encoding.UTF8.GetString(innerPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerPayload);
        }
    }

    public virtual void WriteToWindowsVault(SavedCredential credential)
    {
        if (_useWindowsVault)
            WindowsCredentialVaultInterop.WriteGenericSecret(
                GetWindowsVaultTargetName(credential.Id), credential.EffectiveUserDisplay, credential.ProtectedSecretBase64);
    }

    public virtual void RemoveFromWindowsVault(string credentialId)
    {
        if (_useWindowsVault && !string.IsNullOrWhiteSpace(credentialId))
        {
            WindowsCredentialVaultInterop.DeleteGenericSecret(GetWindowsVaultTargetName(credentialId));
        }
    }

    /// <summary>
    /// Configura, altera ou remove a Senha Mestra re-criptografando todas as credenciais existentes.
    /// </summary>
    public async Task ConfigureMasterPasswordAsync(string? currentMasterPassword, string? newMasterPassword)
    {
        if (string.IsNullOrEmpty(newMasterPassword)) throw new InvalidOperationException(L.Text("A senha mestra é necessária para recuperar os segredos."));
        if (!IsVaultCreated) await CreateVaultAsync(newMasterPassword,newMasterPassword);
        else await ChangeVaultPasswordAsync(currentMasterPassword ?? "",newMasterPassword,newMasterPassword);
    }

    // =========================================================================
    // Criptografia Portátil (Transporte seguro entre computadores via Senha Própria)
    // =========================================================================

    public static byte[] EncryptPortablePayload(byte[] plainUtf8Bytes, string transportPassword)
    {
        if (string.IsNullOrWhiteSpace(transportPassword) || transportPassword.Length < 6)
            throw new ArgumentException(L.Text("A senha de proteção para exportação deve ter pelo menos 6 caracteres."));

        return EncryptPortableV2(plainUtf8Bytes,transportPassword);
    }

    public static byte[] DecryptPortablePayload(byte[] encryptedFileBytes, string transportPassword)
    {
        if (string.IsNullOrWhiteSpace(transportPassword))
            throw new ArgumentException(L.Text("Informe a senha do pacote criptografado."));

        if (encryptedFileBytes.AsSpan().StartsWith("CTIENC02"u8))
        {
            try { return DecryptPortableV2(encryptedFileBytes,transportPassword); }
            catch (CryptographicException ex)
            { throw new CryptographicException(L.Text("Senha incorreta ou pacote criptografado adulterado/corrompido."),ex); }
        }
        var magic = Encoding.ASCII.GetBytes("CTIENC01");
        if (encryptedFileBytes == null || encryptedFileBytes.Length < magic.Length + SaltSizeBytes + NonceSizeBytes + TagSizeBytes)
            throw new InvalidDataException(L.Text("O arquivo selecionado não é um pacote criptografado válido do ConectaTI."));

        for (var i = 0; i < magic.Length; i++)
        {
            if (encryptedFileBytes[i] != magic[i])
                throw new InvalidDataException(L.Text("Cabeçalho de arquivo criptografado inválido (esperado CTIENC01)."));
        }

        var salt = new byte[SaltSizeBytes];
        Buffer.BlockCopy(encryptedFileBytes, magic.Length, salt, 0, SaltSizeBytes);

        var cipherLen = encryptedFileBytes.Length - magic.Length - SaltSizeBytes;
        var cipherWithNonceAndTag = new byte[cipherLen];
        Buffer.BlockCopy(encryptedFileBytes, magic.Length + SaltSizeBytes, cipherWithNonceAndTag, 0, cipherLen);

        var key = DeriveKeyPbkdf2(transportPassword, salt);
        try
        {
            return DecryptAesGcm(cipherWithNonceAndTag, key);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException(L.Text("Senha incorreta ou pacote criptografado adulterado/corrompido."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void EnsureUnlockedForMasterKeyOperation()
    {
        if (IsLocked)
            throw new InvalidOperationException(L.Text("O cofre de credenciais está bloqueado."));

        if (_settingsService.Current.HasMasterPassword && _activeMasterKey == null)
            throw new InvalidOperationException(L.Text("A senha mestra precisa ser validada para acessar os segredos."));
    }

    private static string GetWindowsVaultTargetName(string credentialId) => $"ConectaTI:Credential:{credentialId}";

    private static byte[] DeriveKeyPbkdf2(string password, byte[] salt)
    {
        var bytes=Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes,salt,Pbkdf2Iterations,HashAlgorithmName.SHA256,KeySizeBytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var tag = new byte[TagSizeBytes];
        var ciphertext = new byte[plaintext.Length];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes + TagSizeBytes, ciphertext.Length);
        return result;
    }

    private static byte[] DecryptAesGcm(byte[] combinedNonceTagCipher, byte[] key)
    {
        if (combinedNonceTagCipher.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException(L.Text("Dados criptografados inválidos."));

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var cipherLen = combinedNonceTagCipher.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = new byte[cipherLen];
        var plaintext = new byte[cipherLen];

        Buffer.BlockCopy(combinedNonceTagCipher, 0, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(combinedNonceTagCipher, NonceSizeBytes, tag, 0, TagSizeBytes);
        Buffer.BlockCopy(combinedNonceTagCipher, NonceSizeBytes + TagSizeBytes, ciphertext, 0, cipherLen);

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
