using System.Globalization;
using System.Text;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using Microsoft.Data.Sqlite;

namespace ConectaTI.App.Data;

public class SqliteRepository
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly string _connectionString;

    public SqliteRepository(AppPaths paths, AppLogger logger)
    {
        _paths = paths;
        _logger = logger;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 10
        };
        _connectionString = builder.ToString();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void InitializeAndMigrate()
    {
        _paths.EnsureDirectories();
        using var conn = CreateConnection();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Version INTEGER PRIMARY KEY NOT NULL,
                    Description TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var currentVersion = GetCurrentSchemaVersion(conn);
        const int targetSchemaVersion = 5;

        if (currentVersion > 0 && currentVersion < targetSchemaVersion)
        {
            var preMigrationBackup = Path.Combine(
                _paths.BackupsDirectory,
                $"pre-migration-v{currentVersion}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
            var destBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = preMigrationBackup,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var destConn = new SqliteConnection(destBuilder.ToString());
            destConn.Open();
            conn.BackupDatabase(destConn);
            destConn.Close();
            _logger.Info("Database", $"Backup consistente pré-migração criado em '{preMigrationBackup}'.");
        }

        if (currentVersion < 1)
        {
            ApplyMigration1(conn);
        }

        if (currentVersion < 2)
        {
            ApplyMigration2(conn);
        }

        if (currentVersion < 3)
        {
            ApplyMigration3(conn);
        }

        if (currentVersion < 4)
        {
            ApplyMigration4(conn);
        }
        if (currentVersion < 5)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS PendingVaultCleanup (CredentialId TEXT PRIMARY KEY NOT NULL);
                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (5, 'Recuperação de operações do cofre', $now);
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int GetSchemaVersion()
    {
        using var conn = CreateConnection();
        return GetCurrentSchemaVersion(conn);
    }

    private static int GetCurrentSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private void ApplyMigration1(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Credentials (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL,
                    Username TEXT NOT NULL DEFAULT '',
                    Domain TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    ProtectedSecretBase64 TEXT NOT NULL DEFAULT '',
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Connections (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL,
                    ClientName TEXT NOT NULL,
                    GroupName TEXT NOT NULL DEFAULT 'Geral',
                    Environment INTEGER NOT NULL DEFAULT 0,
                    Protocol INTEGER NOT NULL DEFAULT 0,
                    Host TEXT NOT NULL,
                    Port INTEGER NOT NULL DEFAULT 3389,
                    Username TEXT NOT NULL DEFAULT '',
                    Domain TEXT NOT NULL DEFAULT '',
                    CredentialId TEXT NULL,
                    PrivateKeyPath TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    Tags TEXT NOT NULL DEFAULT '',
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    RdpFullScreen INTEGER NOT NULL DEFAULT 1,
                    RdpWidth INTEGER NOT NULL DEFAULT 1920,
                    RdpHeight INTEGER NOT NULL DEFAULT 1080,
                    RdpUseMultimon INTEGER NOT NULL DEFAULT 0,
                    RdpAdminSession INTEGER NOT NULL DEFAULT 0,
                    SshClient INTEGER NOT NULL DEFAULT 0,
                    SshUseAgent INTEGER NOT NULL DEFAULT 1,
                    VncParameters TEXT NOT NULL DEFAULT '',
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    LastOpenedAtUtc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Connections_ClientName ON Connections(ClientName);
                CREATE INDEX IF NOT EXISTS IX_Connections_Name ON Connections(Name);
                CREATE INDEX IF NOT EXISTS IX_Connections_LastOpened ON Connections(LastOpenedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_Connections_Favorite ON Connections(IsFavorite);

                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (1, 'Estrutura inicial de Conexões e Credenciais', $now);
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
            _logger.Info("Database", "Migração 1 aplicada com sucesso.");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.Error("Database", "Falha ao aplicar Migração 1.", ex);
            throw;
        }
    }

    private void ApplyMigration2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Connections_Protocol_Env ON Connections(Protocol, Environment);

                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (2, 'Índice composto para filtros rápidos de protocolo e ambiente', $now);
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
            _logger.Info("Database", "Migração 2 aplicada com sucesso.");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.Error("Database", "Falha ao aplicar Migração 2.", ex);
            throw;
        }
    }

    private void ApplyMigration3(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.Transaction = tx;
                infoCmd.CommandText = "PRAGMA table_info(Connections);";
                using var reader = infoCmd.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var ddl = new StringBuilder();
            if (!existingColumns.Contains("VpnName"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN VpnName TEXT NOT NULL DEFAULT '';");
            if (!existingColumns.Contains("VpnApp"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN VpnApp TEXT NOT NULL DEFAULT '';");
            if (!existingColumns.Contains("VpnServer"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN VpnServer TEXT NOT NULL DEFAULT '';");
            if (!existingColumns.Contains("VpnNotes"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN VpnNotes TEXT NOT NULL DEFAULT '';");

            ddl.AppendLine("""
                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (3, 'Adiciona campos de VPN do cliente', $now);
                """);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = ddl.ToString();
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.Info("Database", "Migração 3 aplicada com sucesso.");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.Error("Database", "Falha ao aplicar Migração 3.", ex);
            throw;
        }
    }

    private void ApplyMigration4(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.Transaction = tx;
                infoCmd.CommandText = "PRAGMA table_info(Connections);";
                using var reader = infoCmd.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var ddl = new StringBuilder();
            if (!existingColumns.Contains("IsDeleted"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0;");
            if (!existingColumns.Contains("DeletedAtUtc"))
                ddl.AppendLine("ALTER TABLE Connections ADD COLUMN DeletedAtUtc TEXT NULL;");

            ddl.AppendLine("CREATE INDEX IF NOT EXISTS IX_Connections_IsDeleted ON Connections(IsDeleted);");
            ddl.AppendLine("""
                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (4, 'Adiciona exclusão lógica e lixeira com restauração', $now);
                """);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = ddl.ToString();
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.Info("Database", "Migração 4 aplicada com sucesso.");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.Error("Database", "Falha ao aplicar Migração 4.", ex);
            throw;
        }
    }

    // =========================================================================
    // CRUD de Conexões
    // =========================================================================

    public virtual async Task<List<ConnectionEntry>> GetAllConnectionsAsync(bool includeDeleted = false)
    {
        return await Task.Run(() =>
        {
            var list = new List<ConnectionEntry>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT Id, Name, ClientName, GroupName, Environment, Protocol, Host, Port,
                       Username, Domain, CredentialId, PrivateKeyPath, Notes, Tags, IsFavorite,
                       RdpFullScreen, RdpWidth, RdpHeight, RdpUseMultimon, RdpAdminSession,
                       SshClient, SshUseAgent, VncParameters,
                       VpnName, VpnApp, VpnServer, VpnNotes,
                       CreatedAtUtc, UpdatedAtUtc, LastOpenedAtUtc,
                       IsDeleted, DeletedAtUtc
                FROM Connections
                {(includeDeleted ? string.Empty : "WHERE IsDeleted = 0")}
                ORDER BY ClientName COLLATE NOCASE ASC, Name COLLATE NOCASE ASC;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadConnection(reader));
            }
            return list;
        });
    }

    public async Task<List<ConnectionEntry>> GetDeletedConnectionsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<ConnectionEntry>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, ClientName, GroupName, Environment, Protocol, Host, Port,
                       Username, Domain, CredentialId, PrivateKeyPath, Notes, Tags, IsFavorite,
                       RdpFullScreen, RdpWidth, RdpHeight, RdpUseMultimon, RdpAdminSession,
                       SshClient, SshUseAgent, VncParameters,
                       VpnName, VpnApp, VpnServer, VpnNotes,
                       CreatedAtUtc, UpdatedAtUtc, LastOpenedAtUtc,
                       IsDeleted, DeletedAtUtc
                FROM Connections
                WHERE IsDeleted = 1
                ORDER BY DeletedAtUtc DESC, Name COLLATE NOCASE ASC;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadConnection(reader));
            }
            return list;
        });
    }

    public async Task<ConnectionEntry?> GetConnectionByIdAsync(string id)
    {
        return await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, ClientName, GroupName, Environment, Protocol, Host, Port,
                       Username, Domain, CredentialId, PrivateKeyPath, Notes, Tags, IsFavorite,
                       RdpFullScreen, RdpWidth, RdpHeight, RdpUseMultimon, RdpAdminSession,
                       SshClient, SshUseAgent, VncParameters,
                       VpnName, VpnApp, VpnServer, VpnNotes,
                       CreatedAtUtc, UpdatedAtUtc, LastOpenedAtUtc,
                       IsDeleted, DeletedAtUtc
                FROM Connections
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadConnection(reader) : null;
        });
    }

    public async Task SoftDeleteConnectionAsync(string id)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Connections SET IsDeleted = 1, DeletedAtUtc = $now WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
                _logger.Info("Database", $"Conexão ({id}) movida para a lixeira.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao mover conexão ({id}) para a lixeira.", ex);
                throw;
            }
        });
    }

    public async Task RestoreConnectionAsync(string id)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Connections SET IsDeleted = 0, DeletedAtUtc = NULL WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
                _logger.Info("Database", $"Conexão ({id}) restaurada da lixeira.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao restaurar conexão ({id}) da lixeira.", ex);
                throw;
            }
        });
    }

    public async Task PermanentlyDeleteConnectionAsync(string id)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Connections WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
                _logger.Info("Database", $"Conexão ({id}) excluída permanentemente.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao excluir permanentemente a conexão ({id}).", ex);
                throw;
            }
        });
    }

    public async Task<int> EmptyRecycleBinAsync()
    {
        return await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Connections WHERE IsDeleted = 1;";
                var affected = cmd.ExecuteNonQuery();
                tx.Commit();
                _logger.Info("Database", $"Lixeira esvaziada. Total de registros removidos: {affected}.");
                return affected;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", "Erro ao esvaziar lixeira.", ex);
                throw;
            }
        });
    }

    public async Task<int> GetActiveCountAsync()
    {
        return await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Connections WHERE IsDeleted = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    public async Task<int> GetDeletedCountAsync()
    {
        return await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Connections WHERE IsDeleted = 1;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    public async Task SaveConnectionAsync(ConnectionEntry entry)
    {
        await Task.Run(() =>
        {
            entry.UpdatedAtUtc = DateTime.UtcNow;
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                UpsertConnectionCommand(conn, tx, entry);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao salvar conexão '{entry.Name}' ({entry.Id}).", ex);
                throw;
            }
        });
    }

    public async Task SaveConnectionsBatchAsync(IEnumerable<ConnectionEntry> entries)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var entry in entries)
                {
                    if (entry.CreatedAtUtc == default)
                        entry.CreatedAtUtc = DateTime.UtcNow;
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    UpsertConnectionCommand(conn, tx, entry);
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", "Erro ao salvar lote de conexões em transação.", ex);
                throw;
            }
        });
    }

    public async Task DeleteConnectionAsync(string id)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Connections WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao excluir conexão ({id}).", ex);
                throw;
            }
        });
    }

    public async Task MarkConnectionOpenedAsync(string id, DateTime openedAtUtc)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Connections SET LastOpenedAtUtc = $openedAt WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$openedAt", openedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao registrar data de abertura da conexão ({id}).", ex);
                throw;
            }
        });
    }

    private static void UpsertConnectionCommand(SqliteConnection conn, SqliteTransaction tx, ConnectionEntry entry)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Connections (
                Id, Name, ClientName, GroupName, Environment, Protocol, Host, Port,
                Username, Domain, CredentialId, PrivateKeyPath, Notes, Tags, IsFavorite,
                RdpFullScreen, RdpWidth, RdpHeight, RdpUseMultimon, RdpAdminSession,
                SshClient, SshUseAgent, VncParameters,
                VpnName, VpnApp, VpnServer, VpnNotes,
                CreatedAtUtc, UpdatedAtUtc, LastOpenedAtUtc,
                IsDeleted, DeletedAtUtc
            ) VALUES (
                $id, $name, $client, $group, $env, $proto, $host, $port,
                $user, $domain, $credId, $keyPath, $notes, $tags, $fav,
                $rdpFs, $rdpW, $rdpH, $rdpMulti, $rdpAdmin,
                $sshClient, $sshAgent, $vncParams,
                $vpnName, $vpnApp, $vpnServer, $vpnNotes,
                $created, $updated, $lastOpened,
                $isDel, $delAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                ClientName = excluded.ClientName,
                GroupName = excluded.GroupName,
                Environment = excluded.Environment,
                Protocol = excluded.Protocol,
                Host = excluded.Host,
                Port = excluded.Port,
                Username = excluded.Username,
                Domain = excluded.Domain,
                CredentialId = excluded.CredentialId,
                PrivateKeyPath = excluded.PrivateKeyPath,
                Notes = excluded.Notes,
                Tags = excluded.Tags,
                IsFavorite = excluded.IsFavorite,
                RdpFullScreen = excluded.RdpFullScreen,
                RdpWidth = excluded.RdpWidth,
                RdpHeight = excluded.RdpHeight,
                RdpUseMultimon = excluded.RdpUseMultimon,
                RdpAdminSession = excluded.RdpAdminSession,
                SshClient = excluded.SshClient,
                SshUseAgent = excluded.SshUseAgent,
                VncParameters = excluded.VncParameters,
                VpnName = excluded.VpnName,
                VpnApp = excluded.VpnApp,
                VpnServer = excluded.VpnServer,
                VpnNotes = excluded.VpnNotes,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                LastOpenedAtUtc = excluded.LastOpenedAtUtc,
                IsDeleted = excluded.IsDeleted,
                DeletedAtUtc = excluded.DeletedAtUtc;
            """;

        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$name", entry.Name.Trim());
        cmd.Parameters.AddWithValue("$client", entry.ClientName.Trim());
        cmd.Parameters.AddWithValue("$group", string.IsNullOrWhiteSpace(entry.GroupName) ? "Geral" : entry.GroupName.Trim());
        cmd.Parameters.AddWithValue("$env", (int)entry.Environment);
        cmd.Parameters.AddWithValue("$proto", (int)entry.Protocol);
        cmd.Parameters.AddWithValue("$host", entry.Host.Trim());
        cmd.Parameters.AddWithValue("$port", entry.Port);
        cmd.Parameters.AddWithValue("$user", entry.Username?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$domain", entry.Domain?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$credId", string.IsNullOrWhiteSpace(entry.CredentialId) ? DBNull.Value : entry.CredentialId);
        cmd.Parameters.AddWithValue("$keyPath", entry.PrivateKeyPath?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$notes", entry.Notes ?? string.Empty);
        cmd.Parameters.AddWithValue("$tags", entry.Tags?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$fav", entry.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$rdpFs", entry.RdpFullScreen ? 1 : 0);
        cmd.Parameters.AddWithValue("$rdpW", entry.RdpWidth);
        cmd.Parameters.AddWithValue("$rdpH", entry.RdpHeight);
        cmd.Parameters.AddWithValue("$rdpMulti", entry.RdpUseMultimon ? 1 : 0);
        cmd.Parameters.AddWithValue("$rdpAdmin", entry.RdpAdminSession ? 1 : 0);
        cmd.Parameters.AddWithValue("$sshClient", (int)entry.SshClient);
        cmd.Parameters.AddWithValue("$sshAgent", entry.SshUseAgent ? 1 : 0);
        cmd.Parameters.AddWithValue("$vncParams", entry.VncParameters ?? string.Empty);
        cmd.Parameters.AddWithValue("$vpnName", entry.VpnName?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$vpnApp", entry.VpnApp?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$vpnServer", entry.VpnServer?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$vpnNotes", entry.VpnNotes ?? string.Empty);
        cmd.Parameters.AddWithValue("$created", entry.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updated", entry.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$lastOpened", entry.LastOpenedAtUtc.HasValue
            ? entry.LastOpenedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$isDel", entry.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$delAt", entry.DeletedAtUtc.HasValue
            ? entry.DeletedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    private static ConnectionEntry ReadConnection(SqliteDataReader reader)
    {
        return new ConnectionEntry
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            ClientName = reader.GetString(2),
            GroupName = reader.GetString(3),
            Environment = (EnvironmentType)reader.GetInt32(4),
            Protocol = (ConnectionProtocol)reader.GetInt32(5),
            Host = reader.GetString(6),
            Port = reader.GetInt32(7),
            Username = reader.GetString(8),
            Domain = reader.GetString(9),
            CredentialId = reader.IsDBNull(10) ? null : reader.GetString(10),
            PrivateKeyPath = reader.GetString(11),
            Notes = reader.GetString(12),
            Tags = reader.GetString(13),
            IsFavorite = reader.GetInt32(14) != 0,
            RdpFullScreen = reader.GetInt32(15) != 0,
            RdpWidth = reader.GetInt32(16),
            RdpHeight = reader.GetInt32(17),
            RdpUseMultimon = reader.GetInt32(18) != 0,
            RdpAdminSession = reader.GetInt32(19) != 0,
            SshClient = (SshClientPreference)reader.GetInt32(20),
            SshUseAgent = reader.GetInt32(21) != 0,
            VncParameters = reader.GetString(22),
            VpnName = reader.GetString(23),
            VpnApp = reader.GetString(24),
            VpnServer = reader.GetString(25),
            VpnNotes = reader.GetString(26),
            CreatedAtUtc = ParseDateTimeUtc(reader.GetString(27)),
            UpdatedAtUtc = ParseDateTimeUtc(reader.GetString(28)),
            LastOpenedAtUtc = reader.IsDBNull(29) ? null : ParseDateTimeUtc(reader.GetString(29)),
            IsDeleted = reader.FieldCount > 30 && reader.GetInt32(30) != 0,
            DeletedAtUtc = reader.FieldCount > 31 && !reader.IsDBNull(31) ? ParseDateTimeUtc(reader.GetString(31)) : null
        };
    }

    // =========================================================================
    // CRUD de Credenciais
    // =========================================================================

    public virtual async Task<List<SavedCredential>> GetAllCredentialsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<SavedCredential>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, Username, Domain, Notes, ProtectedSecretBase64, CreatedAtUtc, UpdatedAtUtc
                FROM Credentials
                ORDER BY Name COLLATE NOCASE ASC;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SavedCredential
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Username = reader.GetString(2),
                    Domain = reader.GetString(3),
                    Notes = reader.GetString(4),
                    ProtectedSecretBase64 = reader.GetString(5),
                    CreatedAtUtc = ParseDateTimeUtc(reader.GetString(6)),
                    UpdatedAtUtc = ParseDateTimeUtc(reader.GetString(7))
                });
            }
            return list;
        });
    }

    public async Task<SavedCredential?> GetCredentialByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, Username, Domain, Notes, ProtectedSecretBase64, CreatedAtUtc, UpdatedAtUtc
                FROM Credentials
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new SavedCredential
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Username = reader.GetString(2),
                Domain = reader.GetString(3),
                Notes = reader.GetString(4),
                ProtectedSecretBase64 = reader.GetString(5),
                CreatedAtUtc = ParseDateTimeUtc(reader.GetString(6)),
                UpdatedAtUtc = ParseDateTimeUtc(reader.GetString(7))
            };
        });
    }

    public async Task SaveCredentialAsync(SavedCredential cred)
    {
        await Task.Run(() =>
        {
            cred.UpdatedAtUtc = DateTime.UtcNow;
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                UpsertCredentialCommand(conn, tx, cred);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao salvar credencial '{cred.Name}'.", ex);
                throw;
            }
        });
    }

    private static void UpsertCredentialCommand(SqliteConnection conn, SqliteTransaction tx, SavedCredential cred)
    {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO Credentials (
                        Id, Name, Username, Domain, Notes, ProtectedSecretBase64, CreatedAtUtc, UpdatedAtUtc
                    ) VALUES (
                        $id, $name, $user, $domain, $notes, $secret, $created, $updated
                    )
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        Username = excluded.Username,
                        Domain = excluded.Domain,
                        Notes = excluded.Notes,
                        ProtectedSecretBase64 = excluded.ProtectedSecretBase64,
                        UpdatedAtUtc = excluded.UpdatedAtUtc;
                    """;

                cmd.Parameters.AddWithValue("$id", cred.Id);
                cmd.Parameters.AddWithValue("$name", cred.Name.Trim());
                cmd.Parameters.AddWithValue("$user", cred.Username?.Trim() ?? string.Empty);
                cmd.Parameters.AddWithValue("$domain", cred.Domain?.Trim() ?? string.Empty);
                cmd.Parameters.AddWithValue("$notes", cred.Notes ?? string.Empty);
                cmd.Parameters.AddWithValue("$secret", cred.ProtectedSecretBase64 ?? string.Empty);
                cmd.Parameters.AddWithValue("$created", cred.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$updated", cred.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();

    }

    // All SQL changes to a connection and its credential are one unit.
    public virtual Task SaveConnectionWithCredentialAsync(ConnectionEntry entry, SavedCredential? credential)
        => Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            using var old = conn.CreateCommand();
            old.Transaction = tx;
            old.CommandText = "SELECT CredentialId, LastOpenedAtUtc, IsDeleted, DeletedAtUtc FROM Connections WHERE Id = $id;";
            old.Parameters.AddWithValue("$id", entry.Id);
            string? oldCredentialId = null;
            using (var reader = old.ExecuteReader())
            {
                if (reader.Read())
                {
                    oldCredentialId = reader.IsDBNull(0) ? null : reader.GetString(0);
                    entry.LastOpenedAtUtc = reader.IsDBNull(1) ? null : ParseDateTimeUtc(reader.GetString(1));
                    entry.IsDeleted = reader.GetInt32(2) != 0;
                    entry.DeletedAtUtc = reader.IsDBNull(3) ? null : ParseDateTimeUtc(reader.GetString(3));
                }
            }
            if (credential != null) UpsertCredentialCommand(conn, tx, credential);
            entry.UpdatedAtUtc = DateTime.UtcNow;
            UpsertConnectionCommand(conn, tx, entry);
            if (credential != null)
            {
                using var staged = conn.CreateCommand();
                staged.Transaction = tx;
                staged.CommandText = "DELETE FROM PendingVaultCleanup WHERE CredentialId = $id;";
                staged.Parameters.AddWithValue("$id", credential.Id);
                staged.ExecuteNonQuery();
            }
            if (!string.IsNullOrEmpty(oldCredentialId) && oldCredentialId != entry.CredentialId)
            {
                using var cleanup = conn.CreateCommand();
                cleanup.Transaction = tx;
                // Include trash references. Never unlink any other access.
                cleanup.CommandText = """
                    INSERT OR IGNORE INTO PendingVaultCleanup (CredentialId)
                    SELECT $id WHERE NOT EXISTS (SELECT 1 FROM Connections WHERE CredentialId = $id);
                    DELETE FROM Credentials WHERE Id = $id
                    AND NOT EXISTS (SELECT 1 FROM Connections WHERE CredentialId = $id);
                    """;
                cleanup.Parameters.AddWithValue("$id", oldCredentialId);
                cleanup.ExecuteNonQuery();
            }
            tx.Commit();
        });

    public Task QueueVaultCleanupAsync(string credentialId) => Task.Run(() =>
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO PendingVaultCleanup (CredentialId) VALUES ($id);";
        cmd.Parameters.AddWithValue("$id", credentialId);
        cmd.ExecuteNonQuery();
    });

    public Task<List<string>> GetPendingVaultCleanupAsync() => Task.Run(() =>
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CredentialId FROM PendingVaultCleanup
            WHERE NOT EXISTS (SELECT 1 FROM Credentials WHERE Id = CredentialId)
            AND NOT EXISTS (SELECT 1 FROM Connections WHERE Connections.CredentialId = PendingVaultCleanup.CredentialId);
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    });

    public Task CompleteVaultCleanupAsync(string credentialId) => Task.Run(() =>
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingVaultCleanup WHERE CredentialId = $id;";
        cmd.Parameters.AddWithValue("$id", credentialId);
        cmd.ExecuteNonQuery();
    });

    public async Task DeleteCredentialAsync(string id)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var unlinkCmd = conn.CreateCommand())
                {
                    unlinkCmd.Transaction = tx;
                    unlinkCmd.CommandText = "UPDATE Connections SET CredentialId = NULL WHERE CredentialId = $id;";
                    unlinkCmd.Parameters.AddWithValue("$id", id);
                    unlinkCmd.ExecuteNonQuery();
                }

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM Credentials WHERE Id = $id;";
                    delCmd.Parameters.AddWithValue("$id", id);
                    delCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.Error("Database", $"Erro ao excluir credencial ({id}).", ex);
                throw;
            }
        });
    }

    // =========================================================================
    // Backup e Restauração Consistentes
    // =========================================================================

    public async Task<string> CreateBackupAsync(string? destinationFilePath = null, string reasonPrefix = "backup")
    {
        return await Task.Run(() =>
        {
            _paths.EnsureDirectories();
            var targetPath = destinationFilePath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                var fileName = $"{reasonPrefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db";
                targetPath = Path.Combine(_paths.BackupsDirectory, fileName);
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
                Directory.CreateDirectory(targetDir);

            using var sourceConn = CreateConnection();
            var destBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = targetPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var destConn = new SqliteConnection(destBuilder.ToString());
            destConn.Open();

            sourceConn.BackupDatabase(destConn);
            destConn.Close();

            _logger.Info("Backup", $"Backup consistente criado em '{targetPath}'.");
            return targetPath;
        });
    }

    public async Task RestoreBackupAsync(string backupFilePath)
    {
        await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
                throw new FileNotFoundException("O arquivo de backup selecionado não existe.", backupFilePath);

            // 1. Valida se o arquivo de origem é um banco SQLite íntegro e contém a tabela Connections
            ValidateBackupDatabaseFile(backupFilePath);

            // 2. Cria um backup de segurança do estado atual antes de restaurar
            var preRestoreSafetyBackup = await CreateBackupAsync(null, "pre-restore-safety");

            try
            {
                var sourceBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = backupFilePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                };

                using var sourceConn = new SqliteConnection(sourceBuilder.ToString());
                sourceConn.Open();

                using var destConn = CreateConnection();
                sourceConn.BackupDatabase(destConn);
                sourceConn.Close();

                InitializeAndMigrate();
                _logger.Info("Backup", $"Banco restaurado com sucesso a partir de '{backupFilePath}'.");
            }
            catch (Exception ex)
            {
                _logger.Error("Backup", "Erro durante a restauração; revertendo para o backup de segurança.", ex);
                try
                {
                    var safetyBuilder = new SqliteConnectionStringBuilder
                    {
                        DataSource = preRestoreSafetyBackup,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    };
                    using var safetyConn = new SqliteConnection(safetyBuilder.ToString());
                    safetyConn.Open();
                    using var destConn = CreateConnection();
                    safetyConn.BackupDatabase(destConn);
                }
                catch (Exception rollbackEx)
                {
                    _logger.Error("Backup", "Falha crítica no rollback da restauração.", rollbackEx);
                }
                throw;
            }
        });
    }

    public static void ValidateBackupDatabaseFile(string filePath)
    {
        var header = new byte[16];
        using (var fs = File.OpenRead(filePath))
        {
            if (fs.Read(header, 0, 16) < 16)
                throw new InvalidDataException("O arquivo é muito pequeno para ser um banco SQLite válido.");
        }

        var magic = System.Text.Encoding.ASCII.GetString(header, 0, 15);
        if (!string.Equals(magic, "SQLite format 3", StringComparison.Ordinal))
            throw new InvalidDataException("O arquivo selecionado não é um banco de dados SQLite válido.");

        var checkBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        using var conn = new SqliteConnection(checkBuilder.ToString());
        conn.Open();

        using (var integrityCmd = conn.CreateCommand())
        {
            integrityCmd.CommandText = "PRAGMA integrity_check;";
            var integrityResult = integrityCmd.ExecuteScalar()?.ToString();
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Falha na verificação de integridade do SQLite: {integrityResult}");
        }

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Connections';";
            var count = Convert.ToInt32(tableCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count == 0)
                throw new InvalidDataException("O banco SQLite selecionado não possui a estrutura do ConectaTI (tabela Connections ausente).");
        }
    }

    private static DateTime ParseDateTimeUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return DateTime.UtcNow;
    }

    public static List<ConnectionEntry> CreateFictionalDemoConnections()
    {
        var now = DateTime.UtcNow;
        return new List<ConnectionEntry>
        {
            new()
            {
                Name = "SRV-AD-DC01 (Controlador de Domínio)",
                ClientName = "Alfa Logística S.A. (Fictício)",
                GroupName = "Active Directory & DNS",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Rdp,
                Host = "dc01.alfalog.exemplo.local",
                Port = 3389,
                Username = "suporte.ti",
                Domain = "ALFALOG",
                Tags = "ad, dns, windows-server, critico",
                Notes = "Servidor principal de AD e DNS interno (Ambiente Fictício de Demonstração).",
                IsFavorite = true,
                RdpFullScreen = true,
                LastOpenedAtUtc = now.AddHours(-2)
            },
            new()
            {
                Name = "SRV-ERP-APP01 (Aplicação ERP)",
                ClientName = "Alfa Logística S.A. (Fictício)",
                GroupName = "Servidores de Aplicação",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Rdp,
                Host = "10.254.10.21",
                Port = 3389,
                Username = "ALFALOG\\admin.erp",
                Domain = "",
                Tags = "erp, iis, producao",
                Notes = "Servidor de aplicação ERP. Acesso via VPN Alfa.",
                VpnName = "VPN Corporativa Alfa",
                VpnApp = "FortiClient",
                VpnServer = "vpn.alfalog.exemplo.com.br:10443",
                VpnNotes = "Autenticação via MFA FortiToken.",
                IsFavorite = true,
                RdpFullScreen = false,
                RdpWidth = 1600,
                RdpHeight = 900,
                LastOpenedAtUtc = now.AddDays(-1)
            },
            new()
            {
                Name = "GW-FIREWALL-Borda (Linux SSH)",
                ClientName = "Alfa Logística S.A. (Fictício)",
                GroupName = "Redes & Segurança",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Ssh,
                Host = "10.254.10.1",
                Port = 2222,
                Username = "netadmin",
                Tags = "firewall, vpn, linux, ssh",
                Notes = "Gateway SSH na porta 2222. Utilizar chave privada ou Pageant.",
                SshUseAgent = true,
                LastOpenedAtUtc = now.AddHours(-6)
            },
            new()
            {
                Name = "SRV-DB-PG01 (PostgreSQL Linux)",
                ClientName = "Beta Saúde Clínica (Fictício)",
                GroupName = "Banco de Dados",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Ssh,
                Host = "db01.betasaude.exemplo.local",
                Port = 22,
                Username = "postgres_admin",
                Tags = "postgres, linux, ubuntu, db",
                Notes = "Cluster primário PostgreSQL (Dados Fictícios).",
                IsFavorite = true,
                LastOpenedAtUtc = now.AddMinutes(-45)
            },
            new()
            {
                Name = "SRV-SFTP-LAUDOS (Transferência WinSCP)",
                ClientName = "Beta Saúde Clínica (Fictício)",
                GroupName = "Transferência de Arquivos",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Sftp,
                Host = "172.29.40.15",
                Port = 22,
                Username = "operador.sftp",
                Tags = "sftp, winscp, laudos",
                Notes = "Repositório SFTP para coleta de logs e arquivos de integração."
            },
            new()
            {
                Name = "SRV-HML-WEB01 (Homologação Portal)",
                ClientName = "Beta Saúde Clínica (Fictício)",
                GroupName = "Web & API",
                Environment = EnvironmentType.Staging,
                Protocol = ConnectionProtocol.Rdp,
                Host = "hml-web01.betasaude.exemplo.local",
                Port = 33389,
                Username = "dev.suporte@betasaude.exemplo.local",
                Tags = "homologacao, web, rdp",
                Notes = "Ambiente de homologação com porta RDP customizada 33389."
            },
            new()
            {
                Name = "PDV-CAIXA-MATRIZ (Acesso VNC)",
                ClientName = "Gama Varejo Ltda (Fictício)",
                GroupName = "Frente de Loja / PDV",
                Environment = EnvironmentType.Production,
                Protocol = ConnectionProtocol.Vnc,
                Host = "192.168.250.105",
                Port = 5900,
                Username = "suporte",
                Tags = "vnc, pdv, caixa",
                Notes = "Terminal de autoatendimento matriz para suporte visual."
            },
            new()
            {
                Name = "LAB-IPV6-DOCKER01 (Teste IPv6)",
                ClientName = "Gama Varejo Ltda (Fictício)",
                GroupName = "Laboratório",
                Environment = EnvironmentType.Testing,
                Protocol = ConnectionProtocol.Ssh,
                Host = "2001:db8:85a3::8a2e:370:7334",
                Port = 22,
                Username = "devops",
                Tags = "ipv6, docker, lab, teste",
                Notes = "Servidor de testes com endereço IPv6."
            }
        };
    }
}
