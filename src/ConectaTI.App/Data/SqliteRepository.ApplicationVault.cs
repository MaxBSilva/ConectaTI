using System.Security.Cryptography;
using ConectaTI.App.Models;

namespace ConectaTI.App.Data;

public partial class SqliteRepository
{
    internal string? ReadApplicationVaultMetadata()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MetadataJson FROM AppVaultMetadata WHERE Singleton=1";
        return command.ExecuteScalar() as string;
    }

    internal void StoreApplicationVaultMetadata(string metadata, string? expected)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expected == null
            ? "INSERT INTO AppVaultMetadata(Singleton,MetadataJson) VALUES(1,$metadata)"
            : "UPDATE AppVaultMetadata SET MetadataJson=$metadata WHERE Singleton=1 AND MetadataJson=$expected";
        command.Parameters.AddWithValue("$metadata", metadata);
        if (expected != null) command.Parameters.AddWithValue("$expected", expected);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Vault metadata changed.");
        transaction.Commit();
    }

    // Reserve random nonces durably, including those abandoned by a canceled transaction.
    internal Queue<byte[]> ReserveApplicationVaultNonces(string vaultId)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        var result = new Queue<byte[]>();
        while (result.Count < 256)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO AppVaultNonces(VaultId,Nonce) VALUES($vault,$nonce)";
            command.Parameters.AddWithValue("$vault", vaultId);
            command.Parameters.AddWithValue("$nonce", Convert.ToBase64String(nonce));
            if (command.ExecuteNonQuery() == 1) result.Enqueue(nonce);
        }
        transaction.Commit();
        return result;
    }

    internal Task CommitVaultMigrationAsync(IReadOnlyList<SavedCredential> credentials, Action<SavedCredential> verify,
        CancellationToken token) => Task.Run(() =>
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var credential in credentials)
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Credentials SET LegacyProtectedSecretBase64=ProtectedSecretBase64, LegacySecretStorage=SecretStorage,
                    ProtectedSecretBase64=$secret, SecretStorage=2
                WHERE Id=$id AND SecretStorage IN (0,1) AND UpdatedAtUtc=$revision;
                """;
            command.Parameters.AddWithValue("$id", credential.Id);
            command.Parameters.AddWithValue("$secret", credential.ProtectedSecretBase64);
            command.Parameters.AddWithValue("$revision", credential.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Credential changed during migration.");
            using var readback = connection.CreateCommand();
            readback.Transaction = transaction;
            readback.CommandText = "SELECT ProtectedSecretBase64 FROM Credentials WHERE Id=$id";
            readback.Parameters.AddWithValue("$id", credential.Id);
            verify(new SavedCredential { Id=credential.Id, SecretStorage=CredentialSecretStorage.ApplicationVault,
                ProtectedSecretBase64=(string)readback.ExecuteScalar()! });
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }, token);

    internal Task CommitAutomaticMigrationAsync(IReadOnlyList<SavedCredential> credentials, Action<SavedCredential> verify,
        CancellationToken token) => Task.Run(() =>
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var credential in credentials)
        {
            token.ThrowIfCancellationRequested();
            using var history = connection.CreateCommand();
            history.Transaction = transaction;
            history.CommandText = """
                INSERT OR IGNORE INTO CredentialProtectionHistory(CredentialId,SecretStorage,ProtectedSecretBase64)
                SELECT Id,SecretStorage,ProtectedSecretBase64 FROM Credentials WHERE Id=$id;
                """;
            history.Parameters.AddWithValue("$id",credential.Id);
            history.ExecuteNonQuery();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Credentials SET ProtectedSecretBase64=$secret, SecretStorage=1
                WHERE Id=$id AND UpdatedAtUtc=$revision;
                """;
            command.Parameters.AddWithValue("$id",credential.Id);
            command.Parameters.AddWithValue("$secret",credential.ProtectedSecretBase64);
            command.Parameters.AddWithValue("$revision",credential.UpdatedAtUtc.ToString("O",System.Globalization.CultureInfo.InvariantCulture));
            if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Credential changed during migration.");
            using var readback=connection.CreateCommand();
            readback.Transaction=transaction;
            readback.CommandText="SELECT ProtectedSecretBase64 FROM Credentials WHERE Id=$id AND SecretStorage=1";
            readback.Parameters.AddWithValue("$id",credential.Id);
            verify(new SavedCredential {Id=credential.Id,SecretStorage=CredentialSecretStorage.SqliteDpapi,
                ProtectedSecretBase64=(string)readback.ExecuteScalar()!});
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }, token);
}
