using System.Security.Cryptography;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Services;

namespace ConectaTI.App.Security;

/// <summary>
/// Gerencia o armazenamento seguro de credenciais combinando:
/// 1. DPAPI (DataProtectionScope.CurrentUser) com entropia de aplicação.
/// 2. Gerenciador de Credenciais do Windows (Windows Credential Manager).
/// 3. Camada criptográfica adicional AES-256-GCM derivada por PBKDF2-SHA256 (210.000 iterações)
///    quando o usuário ativa uma Senha Mestra.
/// 4. Criptografia autenticada portátil (PBKDF2-SHA256 + AES-256-GCM) para exportação entre computadores.
/// </summary>
public class CredentialVaultService
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

    public event EventHandler? LockStateChanged
    {
        add { }
        remove { }
    }

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

    public bool RequiresMasterPassword => _settingsService.Current.HasMasterPassword;

    public void Lock()
    {
        if (_activeMasterKey != null)
        {
            CryptographicOperations.ZeroMemory(_activeMasterKey);
            _activeMasterKey = null;
        }

        WindowsCredentialVaultInterop.CleanupAllManagedRdpCredentials();
        _logger.Info("Security", "Credenciais temporárias de sessão limpas.");
    }

    public bool Unlock(string? masterPassword = null)
    {
        if (!_settingsService.Current.HasMasterPassword)
        {
            return true;
        }

        if (string.IsNullOrEmpty(masterPassword))
            return false;

        try
        {
            var salt = Convert.FromBase64String(_settingsService.Current.MasterPasswordSaltBase64);
            var candidateKey = DeriveKeyPbkdf2(masterPassword, salt);
            var verifierBytes = Convert.FromBase64String(_settingsService.Current.MasterPasswordVerifierBase64);
            var decryptedVerifier = DecryptAesGcm(verifierBytes, candidateKey);

            if (!CryptographicOperations.FixedTimeEquals(decryptedVerifier, VerifierPlaintext))
            {
                CryptographicOperations.ZeroMemory(candidateKey);
                _logger.Warn("Security", "Tentativa de validação com senha mestra incorreta.");
                return false;
            }

            if (_activeMasterKey != null)
                CryptographicOperations.ZeroMemory(_activeMasterKey);

            _activeMasterKey = candidateKey;
            _logger.Info("Security", "Chave mestra carregada com sucesso.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("Security", $"Falha ao validar senha mestra: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Migra todas as credenciais protegidas por senha mestra anterior para proteção nativa DPAPI do Windows.
    /// </summary>
    public async Task<bool> MigrateFromMasterPasswordAsync(string previousMasterPassword)
    {
        if (!_settingsService.Current.HasMasterPassword)
            return true;

        if (!Unlock(previousMasterPassword))
            return false;

        await ConfigureMasterPasswordAsync(previousMasterPassword, null);
        _logger.Info("Security", "Migração para DPAPI concluída com sucesso. Senha mestra desativada.");
        return true;
    }

    /// <summary>
    /// Protege o segredo de uma credencial para gravação em banco e no Windows Credential Manager.
    /// </summary>
    public string ProtectSecret(string credentialId, string effectiveUsername, string plainPassword, bool writeToWindowsVault = true)
    {
        if (string.IsNullOrEmpty(plainPassword))
            return string.Empty;

        EnsureUnlockedForMasterKeyOperation();

        var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
        try
        {
            byte[] innerPayload;
            string prefix;

            if (_settingsService.Current.HasMasterPassword && _activeMasterKey != null)
            {
                innerPayload = EncryptAesGcm(plainBytes, _activeMasterKey);
                prefix = EnvelopePrefixMasterPlusDpapi;
            }
            else
            {
                innerPayload = plainBytes;
                prefix = EnvelopePrefixDpapiOnly;
            }

            var dpapiBlob = ProtectedData.Protect(
                innerPayload,
                ApplicationDpapiEntropy,
                DataProtectionScope.CurrentUser);

            var finalEnvelope = prefix + Convert.ToBase64String(dpapiBlob);

            if (_useWindowsVault && writeToWindowsVault && !string.IsNullOrWhiteSpace(credentialId))
            {
                WriteToWindowsVault(new SavedCredential { Id = credentialId,
                    Username = effectiveUsername, ProtectedSecretBase64 = finalEnvelope });
            }

            return finalEnvelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// Desprotege o segredo de uma credencial salva.
    /// </summary>
    public string UnprotectSecret(SavedCredential credential)
    {
        if (IsLocked)
            throw new InvalidOperationException("O aplicativo está bloqueado. Desbloqueie antes de acessar credenciais.");

        var envelope = credential.ProtectedSecretBase64;
        if (string.IsNullOrWhiteSpace(envelope) && _useWindowsVault)
        {
            envelope = WindowsCredentialVaultInterop.ReadGenericSecret(GetWindowsVaultTargetName(credential.Id)) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(envelope))
            return string.Empty;

        return UnprotectEnvelopeString(envelope);
    }

    public string UnprotectEnvelopeString(string envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
            return string.Empty;

        EnsureUnlockedForMasterKeyOperation();

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
                    throw new InvalidOperationException("A chave da senha mestra não está carregada na sessão.");

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
        if (_useWindowsVault && !WindowsCredentialVaultInterop.WriteGenericSecret(
            GetWindowsVaultTargetName(credential.Id), credential.EffectiveUserDisplay, credential.ProtectedSecretBase64))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
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
        if (_settingsService.Current.HasMasterPassword)
        {
            if (!Unlock(currentMasterPassword))
                throw new CryptographicException("A senha mestra atual informada está incorreta.");
        }

        // 1. Lê e descriptografa todas as credenciais com a configuração atual
        var allCredentials = await _repository.GetAllCredentialsAsync();
        var plainSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cred in allCredentials)
        {
            plainSecrets[cred.Id] = string.IsNullOrWhiteSpace(cred.ProtectedSecretBase64)
                ? string.Empty
                : UnprotectSecret(cred);
        }

        // 2. Atualiza a chave mestra
        if (string.IsNullOrWhiteSpace(newMasterPassword))
        {
            // Desativa senha mestra (mantém proteção DPAPI do usuário Windows)
            if (_activeMasterKey != null)
            {
                CryptographicOperations.ZeroMemory(_activeMasterKey);
                _activeMasterKey = null;
            }
            _settingsService.Current.HasMasterPassword = false;
            _settingsService.Current.MasterPasswordSaltBase64 = string.Empty;
            _settingsService.Current.MasterPasswordVerifierBase64 = string.Empty;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var newKey = DeriveKeyPbkdf2(newMasterPassword, salt);
            var verifier = EncryptAesGcm(VerifierPlaintext, newKey);

            if (_activeMasterKey != null)
                CryptographicOperations.ZeroMemory(_activeMasterKey);

            _activeMasterKey = newKey;
            _settingsService.Current.HasMasterPassword = true;
            _settingsService.Current.MasterPasswordSaltBase64 = Convert.ToBase64String(salt);
            _settingsService.Current.MasterPasswordVerifierBase64 = Convert.ToBase64String(verifier);
        }

        // 3. Re-protege todas as credenciais com o novo estado
        foreach (var cred in allCredentials)
        {
            var plain = plainSecrets[cred.Id];
            cred.ProtectedSecretBase64 = string.IsNullOrEmpty(plain)
                ? string.Empty
                : ProtectSecret(cred.Id, cred.EffectiveUserDisplay, plain);
            await _repository.SaveCredentialAsync(cred);
        }

        _settingsService.Save();
        _logger.Info("Security", _settingsService.Current.HasMasterPassword
            ? "Senha mestra configurada e cofre re-criptografado."
            : "Senha mestra removida; cofre mantido sob proteção DPAPI do Windows.");
    }

    // =========================================================================
    // Criptografia Portátil (Transporte seguro entre computadores via Senha Própria)
    // =========================================================================

    public static byte[] EncryptPortablePayload(byte[] plainUtf8Bytes, string transportPassword)
    {
        if (string.IsNullOrWhiteSpace(transportPassword) || transportPassword.Length < 6)
            throw new ArgumentException("A senha de proteção para exportação deve ter pelo menos 6 caracteres.");

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = DeriveKeyPbkdf2(transportPassword, salt);
        try
        {
            var cipherWithNonceAndTag = EncryptAesGcm(plainUtf8Bytes, key);
            // Formato binário: Magic "CTIENC01" (8 bytes) + Salt (32 bytes) + Nonce+Tag+Cipher
            var magic = Encoding.ASCII.GetBytes("CTIENC01");
            var output = new byte[magic.Length + salt.Length + cipherWithNonceAndTag.Length];
            Buffer.BlockCopy(magic, 0, output, 0, magic.Length);
            Buffer.BlockCopy(salt, 0, output, magic.Length, salt.Length);
            Buffer.BlockCopy(cipherWithNonceAndTag, 0, output, magic.Length + salt.Length, cipherWithNonceAndTag.Length);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] DecryptPortablePayload(byte[] encryptedFileBytes, string transportPassword)
    {
        if (string.IsNullOrWhiteSpace(transportPassword))
            throw new ArgumentException("Informe a senha do pacote criptografado.");

        var magic = Encoding.ASCII.GetBytes("CTIENC01");
        if (encryptedFileBytes == null || encryptedFileBytes.Length < magic.Length + SaltSizeBytes + NonceSizeBytes + TagSizeBytes)
            throw new InvalidDataException("O arquivo selecionado não é um pacote criptografado válido do ConectaTI.");

        for (var i = 0; i < magic.Length; i++)
        {
            if (encryptedFileBytes[i] != magic[i])
                throw new InvalidDataException("Cabeçalho de arquivo criptografado inválido (esperado CTIENC01).");
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
            throw new CryptographicException("Senha incorreta ou pacote criptografado adulterado/corrompido.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void EnsureUnlockedForMasterKeyOperation()
    {
        if (IsLocked)
            throw new InvalidOperationException("O cofre de credenciais está bloqueado.");

        if (_settingsService.Current.HasMasterPassword && _activeMasterKey == null)
            throw new InvalidOperationException("A senha mestra precisa ser validada para acessar os segredos.");
    }

    private static string GetWindowsVaultTargetName(string credentialId) => $"ConectaTI:Credential:{credentialId}";

    private static byte[] DeriveKeyPbkdf2(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
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
            throw new CryptographicException("Dados criptografados inválidos.");

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
