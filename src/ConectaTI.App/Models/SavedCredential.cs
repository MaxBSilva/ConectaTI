using System.Text.Json.Serialization;
using ConectaTI.App.Helpers;

namespace ConectaTI.App.Models;

/// <summary>
/// Representa metadados de uma credencial reutilizável.
/// A senha nunca fica em texto puro no banco: é armazenada protegida via DPAPI / Windows Credential Manager / AES-GCM.
/// </summary>
public enum CredentialSecretStorage
{
    LegacyWindows = 0,
    SqliteDpapi = 1,
    ApplicationVault = 2
}

public class SavedCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Blob criptografado (DPAPI + opcionalmente camada AES-256-GCM da senha mestra) em Base64.
    /// Nunca contém texto puro.
    /// </summary>
    public string ProtectedSecretBase64 { get; set; } = string.Empty;

    // Explicit origin: an empty SQLite value never falls back to an older Windows copy.
    public CredentialSecretStorage SecretStorage { get; set; } = CredentialSecretStorage.SqliteDpapi;

    public string LegacyProtectedSecretBase64 { get; set; } = string.Empty;
    public CredentialSecretStorage? LegacySecretStorage { get; set; }

    [JsonIgnore]
    public string StorageLocationDisplay => SecretStorage == CredentialSecretStorage.LegacyWindows
        ? L.Text("Windows Credential Manager (legado)")
        : SecretStorage == CredentialSecretStorage.SqliteDpapi ? L.Text("SQLite — DPAPI do usuário Windows") : L.Text("Cofre próprio — AES-256-GCM");

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string EffectiveUserDisplay => UserIdentityFormatter.FormatDisplay(Username, Domain);

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(EffectiveUserDisplay)
        ? Name
        : $"{Name} ({EffectiveUserDisplay})";
}
