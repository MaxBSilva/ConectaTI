using ConectaTI.App.Models;
using ConectaTI.App.Services.ImportExport;

namespace ConectaTI.App.Data;

public sealed record BatchEditPatch(bool ChangeClient, string Client, bool ChangeGroup, string Group,
    bool ChangeEnvironment, EnvironmentType Environment);

public partial class SqliteRepository
{
    public Task QueueVaultCleanupBatchAsync(IEnumerable<string> ids) => Task.Run(() =>
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO PendingVaultCleanup (CredentialId) VALUES ($id)";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    });

    // A whole confirmed import, including its credential links, has a single commit point.
    public virtual Task CommitImportBatchAsync(IReadOnlyList<ConnectionEntry> entries,
        IReadOnlyList<SavedCredential> credentials, CancellationToken token = default,
        IProgress<ImportProgress>? progress = null) => Task.Run(() =>
    {
        var stage = "OpenConnection";
        int? recordIndex = null, credentialIndex = null;
        try
        {
            using var connection = CreateConnection();
            stage = "BeginTransaction";
            using var transaction = connection.BeginTransaction();
            var retired = new HashSet<string>();
            foreach (var credential in credentials)
            {
                token.ThrowIfCancellationRequested();
                stage = "UpsertCredential"; credentialIndex = (credentialIndex ?? 0) + 1;
                UpsertCredentialCommand(connection, transaction, credential);
            }
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                stage = "ReadExistingConnection"; recordIndex = i + 1; credentialIndex = null;
                var entry = entries[i];
                using var old = connection.CreateCommand();
                old.Transaction = transaction;
                old.CommandText = "SELECT CredentialId, CreatedAtUtc, LastOpenedAtUtc, IsDeleted, DeletedAtUtc FROM Connections WHERE Id=$id";
                old.Parameters.AddWithValue("$id", entry.Id);
                using (var reader = old.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        if (!reader.IsDBNull(0) && reader.GetString(0) != entry.CredentialId) retired.Add(reader.GetString(0));
                        entry.CreatedAtUtc = ParseDateTimeUtc(reader.GetString(1));
                        entry.LastOpenedAtUtc = reader.IsDBNull(2) ? null : ParseDateTimeUtc(reader.GetString(2));
                        entry.IsDeleted = reader.GetInt32(3) != 0;
                        entry.DeletedAtUtc = reader.IsDBNull(4) ? null : ParseDateTimeUtc(reader.GetString(4));
                    }
                }
                stage = "UpsertConnection";
                UpsertConnectionCommand(connection, transaction, entry);
                if (i % 25 == 0 || i + 1 == entries.Count) progress?.Report(new("Importing", i + 1, entries.Count));
            }
            stage = "CompletePreparedCleanup"; recordIndex = null; credentialIndex = null;
            foreach (var credential in credentials)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM PendingVaultCleanup WHERE CredentialId=$id";
                cmd.Parameters.AddWithValue("$id", credential.Id);
                cmd.ExecuteNonQuery();
            }
            stage = "RetireUnreferencedCredentials";
            foreach (var id in retired)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    DELETE FROM Credentials WHERE Id=$id AND NOT EXISTS(SELECT 1 FROM Connections WHERE CredentialId=$id);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Committing", entries.Count, entries.Count));
            token.ThrowIfCancellationRequested();
            stage = "Commit";
            transaction.Commit(); // Cancellation after this line cannot change the successful outcome.
        }
        catch (Exception exception)
        {
            _logger.RecordFailure("Import.SQLite." + stage, exception, count: entries.Count,
                recordIndex: recordIndex, credentialIndex: credentialIndex);
            throw;
        }
    }, token);

    // Update only selected columns, so neither drafts nor stale UI snapshots can overwrite secrets.
    public virtual Task<int> ApplyBatchEditAsync(IReadOnlyCollection<string> ids, BatchEditPatch patch,
        CancellationToken token = default) => Task.Run(() =>
    {
        if (!patch.ChangeClient && !patch.ChangeGroup && !patch.ChangeEnvironment) return 0;
        if (patch.ChangeEnvironment && patch.Environment is not (EnvironmentType.Production or EnvironmentType.Staging or EnvironmentType.Testing))
            throw new ArgumentException("Invalid environment");
        if (patch.Client.Length > 1024 || patch.Group.Length > 1024) throw new ArgumentException("Field too long");
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var columns = new List<string> { "UpdatedAtUtc=$now" };
            if (patch.ChangeClient) columns.Add("ClientName=$client");
            if (patch.ChangeGroup) columns.Add("GroupName=$group");
            if (patch.ChangeEnvironment) columns.Add("Environment=$environment");
            command.CommandText = $"UPDATE Connections SET {string.Join(",", columns)} WHERE Id=$id AND IsDeleted=0";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (patch.ChangeClient) command.Parameters.AddWithValue("$client", patch.Client);
            if (patch.ChangeGroup) command.Parameters.AddWithValue("$group", patch.Group);
            if (patch.ChangeEnvironment) command.Parameters.AddWithValue("$environment", (int)patch.Environment);
            count += command.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return count;
    }, token);
}
