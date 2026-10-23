using System.Globalization;
using ConectaTI.App.Services;
using Microsoft.Data.Sqlite;

namespace ConectaTI.App.Data;

public record DatabaseCandidateInfo(string Path, bool IsValid, int ConnectionsCount, int CredentialsCount, DateTime LastModified);

public record MigrationConflictInfo(
    DatabaseCandidateInfo ExistingTarget,
    IReadOnlyList<DatabaseCandidateInfo> LegacyCandidates);

public enum MigrationConflictChoice
{
    KeepTarget,
    ReplaceTargetWithSelectedLegacy,
    ImportFromLegacy
}

public record MigrationConflictResolution(MigrationConflictChoice Choice, string? SelectedLegacyPath);

public static class DatabaseMigrationManager
{
    public static void EnsureDatabaseMigrated(
        AppPaths paths,
        AppLogger logger,
        Func<MigrationConflictInfo, MigrationConflictResolution>? conflictResolver = null)
    {
        try
        {
            var targetPath = paths.DatabasePath;
            var targetExists = File.Exists(targetPath);

            var legacyCandidates = DiscoverLegacyCandidates(paths)
                .Where(c => c.IsValid && c.ConnectionsCount > 0)
                .ToList();

            if (legacyCandidates.Count == 0)
            {
                // Nenhuma base legada para migrar
                return;
            }

            // Caso 1: Destino (sqlite.db) não existe
            if (!targetExists)
            {
                if (legacyCandidates.Count == 1)
                {
                    // Migração automática e transparente de uma única base legada
                    var singleSource = legacyCandidates[0];
                    PerformSafeMigration(singleSource.Path, targetPath, paths, logger);
                    return;
                }

                // Múltiplas bases legadas encontradas
                if (conflictResolver != null)
                {
                    var dummyTarget = new DatabaseCandidateInfo(targetPath, false, 0, 0, DateTime.MinValue);
                    var resolution = conflictResolver(new MigrationConflictInfo(dummyTarget, legacyCandidates));
                    if (!string.IsNullOrWhiteSpace(resolution.SelectedLegacyPath) && File.Exists(resolution.SelectedLegacyPath))
                    {
                        PerformSafeMigration(resolution.SelectedLegacyPath, targetPath, paths, logger);
                        return;
                    }
                }

                // Fallback: seleciona a mais recente com mais registros
                var bestSource = legacyCandidates
                    .OrderByDescending(c => c.ConnectionsCount)
                    .ThenByDescending(c => c.LastModified)
                    .First();

                PerformSafeMigration(bestSource.Path, targetPath, paths, logger);
                return;
            }

            // Caso 2: Destino (sqlite.db) já existe
            var targetInfo = InspectDatabase(targetPath);
            if (targetInfo.ConnectionsCount == 0 && legacyCandidates.Count > 0)
            {
                // Destino estava vazio mas há dados legados
                var bestSource = legacyCandidates
                    .OrderByDescending(c => c.ConnectionsCount)
                    .ThenByDescending(c => c.LastModified)
                    .First();

                PerformSafeMigration(bestSource.Path, targetPath, paths, logger);
                return;
            }

            // Se destino já tem registros e há bases legadas com caminhos diferentes,
            // aciona o resolver de conflito se disponível para permitir importação controlada
            if (conflictResolver != null)
            {
                var differentLegacy = legacyCandidates
                    .Where(l => !string.Equals(Path.GetFullPath(l.Path), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (differentLegacy.Count > 0)
                {
                    var resolution = conflictResolver(new MigrationConflictInfo(targetInfo, differentLegacy));
                    if (resolution.Choice == MigrationConflictChoice.ReplaceTargetWithSelectedLegacy &&
                        !string.IsNullOrWhiteSpace(resolution.SelectedLegacyPath))
                    {
                        PerformSafeMigration(resolution.SelectedLegacyPath, targetPath, paths, logger);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("Migration", "Falha durante o processo de migração de banco de dados.", ex);
        }
    }

    public static List<DatabaseCandidateInfo> DiscoverLegacyCandidates(AppPaths paths)
    {
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. conectati.db ao lado do executável / na pasta raiz
        var localLegacy = Path.Combine(paths.RootDirectory, "conectati.db");
        if (File.Exists(localLegacy))
        {
            candidatePaths.Add(Path.GetFullPath(localLegacy));
        }

        // 2. conectati.db em %LOCALAPPDATA%\ConectaTI\
        var appDataBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConectaTI",
            "conectati.db");
        if (File.Exists(appDataBase))
        {
            candidatePaths.Add(Path.GetFullPath(appDataBase));
        }

        // 3. sqlite.db em %LOCALAPPDATA%\ConectaTI\ caso o executável tenha sido movido para outra pasta
        var appDataSqlite = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConectaTI",
            "sqlite.db");
        if (File.Exists(appDataSqlite) && !string.Equals(Path.GetFullPath(appDataSqlite), Path.GetFullPath(paths.DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            candidatePaths.Add(Path.GetFullPath(appDataSqlite));
        }

        var results = new List<DatabaseCandidateInfo>();
        foreach (var path in candidatePaths)
        {
            results.Add(InspectDatabase(path));
        }
        return results;
    }

    public static DatabaseCandidateInfo InspectDatabase(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return new DatabaseCandidateInfo(dbPath, false, 0, 0, DateTime.MinValue);
        }

        var lastModified = File.GetLastWriteTime(dbPath);
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            };

            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();

            using (var integrityCmd = conn.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check;";
                var res = integrityCmd.ExecuteScalar()?.ToString();
                if (!string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new DatabaseCandidateInfo(dbPath, false, 0, 0, lastModified);
                }
            }

            int connCount = 0;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Connections';";
                var hasTable = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                if (hasTable)
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM Connections;";
                    connCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }

            int credCount = 0;
            using (var credCmd = conn.CreateCommand())
            {
                credCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Credentials';";
                var hasCredTable = Convert.ToInt32(credCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                if (hasCredTable)
                {
                    credCmd.CommandText = "SELECT COUNT(*) FROM Credentials;";
                    credCount = Convert.ToInt32(credCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }

            return new DatabaseCandidateInfo(dbPath, true, connCount, credCount, lastModified);
        }
        catch
        {
            return new DatabaseCandidateInfo(dbPath, false, 0, 0, lastModified);
        }
    }

    public static void PerformSafeMigration(
        string sourceDbPath,
        string targetDbPath,
        AppPaths paths,
        AppLogger logger)
    {
        paths.EnsureDirectories();

        var sourceInfo = InspectDatabase(sourceDbPath);
        if (!sourceInfo.IsValid)
        {
            throw new InvalidOperationException($"A base de origem '{sourceDbPath}' não é um banco SQLite íntegro.");
        }

        // 1. Cria backup consistente de segurança da origem na pasta de backups
        var backupPath = Path.Combine(
            paths.BackupsDirectory,
            $"migration-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");

        var srcBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using (var srcConn = new SqliteConnection(srcBuilder.ToString()))
        {
            srcConn.Open();

            var backupBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var backupConn = new SqliteConnection(backupBuilder.ToString());
            backupConn.Open();
            srcConn.BackupDatabase(backupConn);
            backupConn.Close();
        }
        logger.Info("Migration", $"Backup da base de origem criado em '{backupPath}'.");

        // 2. Copia consistente para o arquivo sqlite.db destino via SQLite Online Backup (garante captura completa do WAL)
        var tempDest = targetDbPath + ".migrating.tmp";
        if (File.Exists(tempDest)) File.Delete(tempDest);

        using (var srcConn = new SqliteConnection(srcBuilder.ToString()))
        {
            srcConn.Open();

            var destBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = tempDest,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var destConn = new SqliteConnection(destBuilder.ToString());
            destConn.Open();
            srcConn.BackupDatabase(destConn);
            destConn.Close();
        }

        // 3. Valida integridade e contagem no arquivo temporário antes de substituir o destino final
        var migratedInfo = InspectDatabase(tempDest);
        if (!migratedInfo.IsValid || migratedInfo.ConnectionsCount != sourceInfo.ConnectionsCount)
        {
            if (File.Exists(tempDest)) File.Delete(tempDest);
            throw new InvalidOperationException(
                $"Falha de validação pós-cópia: conexões na origem={sourceInfo.ConnectionsCount}, no destino={migratedInfo.ConnectionsCount}.");
        }

        // 4. Move o arquivo temporário validado para o caminho definitivo
        if (File.Exists(targetDbPath))
        {
            File.Delete(targetDbPath);
        }
        File.Move(tempDest, targetDbPath);

        // 5. Copia settings.json legado para o destino se ainda não existir
        var sourceDir = Path.GetDirectoryName(sourceDbPath);
        if (!string.IsNullOrWhiteSpace(sourceDir))
        {
            var legacySettings = Path.Combine(sourceDir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(paths.SettingsPath))
            {
                try
                {
                    File.Copy(legacySettings, paths.SettingsPath, overwrite: false);
                    logger.Info("Migration", $"Configurações copiadas de '{legacySettings}' para '{paths.SettingsPath}'.");
                }
                catch
                {
                }
            }
        }

        logger.Info("Migration", $"Migração concluída com sucesso: '{sourceDbPath}' -> '{targetDbPath}' ({sourceInfo.ConnectionsCount} conexões, {sourceInfo.CredentialsCount} credenciais).");
    }
}
