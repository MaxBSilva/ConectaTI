using System.Globalization;
using System.Text;
using ConectaTI.App.Services;
using Microsoft.Data.Sqlite;

namespace ConectaTI.App.Data;

// Schema evolution owns its backups and transactions; CRUD never contains DDL.
internal sealed class SqliteSchemaMigrator(AppPaths paths, AppLogger logger)
{
    public const int CurrentVersion = 11;
    private readonly AppPaths _paths = paths;
    private readonly AppLogger _logger = logger;

    public void Migrate(SqliteConnection conn)
    {

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

        var currentVersion = ReadVersion(conn);
        const int targetSchemaVersion = CurrentVersion;

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
        if (currentVersion < 6)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE Credentials ADD COLUMN SecretStorage INTEGER NOT NULL DEFAULT 1 CHECK(SecretStorage IN (0,1));
                UPDATE Credentials SET SecretStorage = CASE WHEN trim(ProtectedSecretBase64) = '' THEN 0 ELSE 1 END;
                ALTER TABLE PendingVaultCleanup ADD COLUMN VerifiedSqliteCopy INTEGER NOT NULL DEFAULT 0;
                INSERT INTO SchemaMigrations (Version, Description, AppliedAtUtc)
                VALUES (6, 'Origem explícita dos segredos DPAPI no SQLite e compatibilidade Windows', $now);
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        if (currentVersion < 7)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE CredentialsVaultV7 (
                    Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL, Username TEXT NOT NULL DEFAULT '',
                    Domain TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '',
                    ProtectedSecretBase64 TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                    SecretStorage INTEGER NOT NULL DEFAULT 2 CHECK(SecretStorage IN (0,1,2)),
                    LegacyProtectedSecretBase64 TEXT NOT NULL DEFAULT '', LegacySecretStorage INTEGER NULL);
                INSERT INTO CredentialsVaultV7
                    (Id,Name,Username,Domain,Notes,ProtectedSecretBase64,CreatedAtUtc,UpdatedAtUtc,SecretStorage)
                SELECT Id,Name,Username,Domain,Notes,ProtectedSecretBase64,CreatedAtUtc,UpdatedAtUtc,SecretStorage FROM Credentials;
                DROP TABLE Credentials;
                ALTER TABLE CredentialsVaultV7 RENAME TO Credentials;
                CREATE TABLE AppVaultMetadata (Singleton INTEGER PRIMARY KEY CHECK(Singleton=1), MetadataJson TEXT NOT NULL);
                CREATE TABLE AppVaultNonces (VaultId TEXT NOT NULL, Nonce TEXT NOT NULL, PRIMARY KEY(VaultId,Nonce));
                INSERT INTO SchemaMigrations(Version,Description,AppliedAtUtc)
                VALUES(7,'Cofre próprio AES-GCM e chave de dados protegida por senha mestra',$now);
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        if (currentVersion < 8)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                CREATE TABLE CredentialProtectionHistory(
                    CredentialId TEXT NOT NULL, SecretStorage INTEGER NOT NULL, ProtectedSecretBase64 TEXT NOT NULL,
                    PRIMARY KEY(CredentialId,SecretStorage,ProtectedSecretBase64));
                INSERT INTO SchemaMigrations(Version,Description,AppliedAtUtc)
                VALUES(8,'Proteção automática CurrentUser e histórico de migração',$now);
                """;
            cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        if (currentVersion < 9) ApplyRdpMigration(conn);
        if (currentVersion < 10) ApplyActivityMigration(conn);
        if (currentVersion < 11) ApplyVpnCredentialMigration(conn);
    }

    private static void ApplyVpnCredentialMigration(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connections') WHERE name='VpnCredentialId';";
        var hasColumn = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        cmd.CommandText = (hasColumn ? "" : "ALTER TABLE Connections ADD COLUMN VpnCredentialId TEXT NULL; ")
            + "INSERT INTO SchemaMigrations VALUES(11,'Credencial opcional vinculada à VPN do cliente',$now);";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void ApplyActivityMigration(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connections') WHERE name='IsActive';";
        var hasColumn = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        cmd.CommandText = (hasColumn ? "" : "ALTER TABLE Connections ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1 CHECK(IsActive IN (0,1)); ")
            + "INSERT INTO SchemaMigrations VALUES(10,'Classificação independente Ativo/Inativo',$now);";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
    private void ApplyRdpMigration(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        using var info = conn.CreateCommand();
        info.Transaction = tx;
        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connections') WHERE name='RdpOptionsJson';";
        var hasColumn = Convert.ToInt32(info.ExecuteScalar()) > 0;
        cmd.CommandText = (hasColumn ? "" : "ALTER TABLE Connections ADD COLUMN RdpOptionsJson TEXT NOT NULL DEFAULT '{}'; ")
            + "INSERT INTO SchemaMigrations VALUES(9,'RD Gateway e propriedades RDP',$now);";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    internal static int ReadVersion(SqliteConnection conn)
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

}
