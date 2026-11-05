using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class RefactorBoundaryTests : IDisposable
{
    private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Refactor_" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CredentialBoundaryDiscardsLateSecretsAndFailuresWithoutSqliteOrDpapi(bool failure)
    {
        var lookup = new Lookup(); var reader = new Reader(); var clipboard = new MemoryClipboard();
        var service = new ConnectionCredentialService(lookup, reader, clipboard);
        var current = true;
        var pending = service.ReadAsync("synthetic-id", () => current);
        await reader.Entered.Task;
        current = false;
        if (failure) reader.Completion.SetException(new IOException("Synthetic failure"));
        else reader.Completion.SetResult("SYNTHETIC_OLD_SECRET");
        var result = await pending;
        Assert.Equal(CredentialReadStatus.Obsolete, result.Status);
        Assert.Null(result.Secret); Assert.Null(result.Error); Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task CredentialCancellationAndMissingLinkNeverPublishASecret()
    {
        var reader = new Reader(); var clipboard = new MemoryClipboard();
        var service = new ConnectionCredentialService(new Lookup(), reader, clipboard);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ReadAsync("synthetic-id", () => true, cancellation.Token);
        await reader.Entered.Task;
        cancellation.Cancel(); reader.Completion.SetResult("SYNTHETIC_CANCELLED");
        var result = await pending;
        Assert.Equal(CredentialReadStatus.Cancelled, result.Status); Assert.Null(result.Secret);
        Assert.DoesNotContain("SYNTHETIC_CANCELLED", result.ToString());
        var missing = await service.ReadAsync(null, () => true);
        Assert.Equal(CredentialReadStatus.Missing, missing.Status);
        Assert.False(await service.CopyPasswordAsync("SYNTHETIC", () => false, 30));
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task CredentialBoundaryCopiesCurrentDataThroughClipboardContract()
    {
        var reader = new Reader(); reader.Completion.SetResult("SYNTHETIC_CURRENT");
        var clipboard = new MemoryClipboard();
        var service = new ConnectionCredentialService(new Lookup(), reader, clipboard);
        var result = await service.ReadAsync("synthetic-id", () => true);
        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.DoesNotContain("SYNTHETIC_CURRENT", result.ToString());
        Assert.True(await service.CopyConnectionAsync(new() { Name = "Unsaved", Host = "draft.example.test" },
            "draft.example.test", "synthetic", result.Secret, () => true, 19));
        Assert.Contains("Unsaved", clipboard.Text); Assert.Contains("SYNTHETIC_CURRENT", clipboard.Text);
        Assert.True(clipboard.IsSecret); Assert.Equal(19, clipboard.ClearSeconds);
    }

    [Fact]
    public async Task CompletedEmptyReadStillAllowsCompleteCopyWithoutPasswordAsInReference()
    {
        var reader = new Reader(); reader.Completion.SetResult("");
        var clipboard = new MemoryClipboard();
        var service = new ConnectionCredentialService(new Lookup(), reader, clipboard);
        var result = await service.ReadAsync("synthetic-id", () => true);
        Assert.Equal(CredentialReadStatus.Available, result.Status);
        Assert.True(await service.CopyConnectionAsync(new() { Name = "Synthetic" }, "host.example.test", "",
            result.Secret, () => true, 30));
        Assert.Contains("Synthetic", clipboard.Text);
        Assert.DoesNotContain("Senha:", clipboard.Text); Assert.DoesNotContain("Password:", clipboard.Text);
    }

    [Fact]
    public void PlanBuilderCanProtectRdpWithoutCallingWindowsOrStartingProcesses()
    {
        var encoder = new PasswordEncoder();
        var builder = new ConnectionPlanBuilder(paths, encoder);
        var settings = new AppSettings();
        var entry = new ConnectionEntry { Name = "ERP fictício", Host = "rdp.example.test", Port = 3390, RdpAdminSession = true };
        var plan = builder.Build(entry, settings, decryptedPassword: "SYNTHETIC_RDP");
        Assert.Contains("password 51:b:ABCDEF", plan.TempRdpContent);
        Assert.DoesNotContain("SYNTHETIC_RDP", plan.TempRdpContent);
        Assert.Equal(plan.TempRdpFilePath, plan.Arguments[0]); Assert.Equal("/admin", plan.Arguments[1]);
        var preview = builder.Build(entry, settings, null, true);
        Assert.DoesNotContain("password 51", preview.TempRdpContent);
        Assert.Equal(1, encoder.Calls); Assert.NotEqual(plan.TempRdpFilePath, preview.TempRdpFilePath);
        Assert.False(File.Exists(plan.TempRdpFilePath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProcessBoundaryMarksOpenedOnlyAfterSuccessfulStart(bool fail)
    {
        var logger = new AppLogger(paths); var repo = new SqliteRepository(paths, logger); repo.InitializeAndMigrate();
        var settings = new SettingsService(paths, logger); settings.Load();
        settings.Current.PuttyExecutablePath = Environment.ProcessPath!;
        var runner = new ProcessRunner { Fail = fail };
        var launcher = new ConnectionLauncherService(paths, settings, repo, new Reader(), logger, runner);
        var entry = new ConnectionEntry { Name = "Synthetic SSH", Host = "host.example.test", Port = 2222,
            Protocol = ConnectionProtocol.Ssh, SshClient = SshClientPreference.Putty, Username = "synthetic" };
        await repo.SaveConnectionAsync(entry);
        var result = await launcher.LaunchAsync(entry);
        Assert.Equal(!fail, result.Started);
        Assert.NotNull(runner.Captured); Assert.False(runner.Captured.UseShellExecute);
        Assert.Contains("2222", runner.Captured.ArgumentList); Assert.Contains("host.example.test", runner.Captured.ArgumentList);
        Assert.Equal(!fail, (await repo.GetConnectionByIdAsync(entry.Id))!.LastOpenedAtUtc.HasValue);
    }

    [Fact]
    public async Task SchemaBoundaryRollsBackFailedStepAndCanRetryWithoutLosingRecords()
    {
        var logger = new AppLogger(paths); var repo = new SqliteRepository(paths, logger); repo.InitializeAndMigrate();
        var credential = new SavedCredential { Name = "Synthetic", ProtectedSecretBase64 = "opaque-synthetic-envelope" };
        await repo.SaveCredentialAsync(credential);
        var entry = new ConnectionEntry { Name = "Synthetic", Host = "host.example.test", CredentialId = credential.Id };
        await repo.SaveConnectionAsync(entry);
        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE Connections DROP COLUMN RdpOptionsJson;
            DELETE FROM SchemaMigrations WHERE Version>=9;
            CREATE TRIGGER RejectVersionNine BEFORE INSERT ON SchemaMigrations WHEN NEW.Version=9
            BEGIN SELECT RAISE(ABORT, 'Synthetic migration failure'); END;
            """;
        command.ExecuteNonQuery();
        var migrator = new SqliteSchemaMigrator(paths, logger);
        Assert.Throws<SqliteException>(() => migrator.Migrate(connection));
        Assert.Equal(8, SqliteSchemaMigrator.ReadVersion(connection));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connections') WHERE name='RdpOptionsJson';";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "DROP TRIGGER RejectVersionNine;"; command.ExecuteNonQuery();
        migrator.Migrate(connection);
        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, SqliteSchemaMigrator.ReadVersion(connection));
        migrator.Migrate(connection);
        Assert.Equal(credential.Id, (await repo.GetConnectionByIdAsync(entry.Id))!.CredentialId);
        Assert.Equal("opaque-synthetic-envelope", (await repo.GetCredentialByIdAsync(credential.Id))!.ProtectedSecretBase64);
        Assert.NotEmpty(Directory.GetFiles(paths.BackupsDirectory, "pre-migration-v8-*.db"));
    }

    [Fact]
    public async Task OutsideTrashCountKeepsSerializedCompatibilityAndIncludesInactiveRecords()
    {
        var logger = new AppLogger(paths); var repo = new SqliteRepository(paths, logger); repo.InitializeAndMigrate();
        await repo.SaveConnectionsBatchAsync([
            new() { Name = "Active", Host = "a.example.test" },
            new() { Name = "Inactive", Host = "i.example.test", IsActive = false },
            new() { Name = "Trash", Host = "t.example.test", IsDeleted = true }
        ]);
        var diagnostic = await new AppDiagnosticService(paths, repo, logger).CollectDiagnosticInfoAsync();
        Assert.Equal(2, diagnostic.ConnectionsOutsideTrashCount);
        Assert.Equal(await repo.GetActiveCountAsync(), await repo.GetConnectionsOutsideTrashCountAsync());
        var json = JsonSerializer.Serialize(diagnostic);
        Assert.Contains("\"ActiveConnectionsCount\":2", json);
        Assert.DoesNotContain("\"ConnectionsOutsideTrashCount\"", json);
    }

    [Fact]
    public void NativeCodecContractsRetainHeadersLegacyAttributesAndSerializedFieldNames()
    {
        var csv = ConnectionCsvCodec.Write([new() { Name = "Synthetic", Host = "host.example.test", SshUseAgent = false }]);
        Assert.StartsWith("Nome;Cliente;Grupo;Ambiente;Protocolo;Host;Porta;Usuario;Dominio;ChavePrivada;", csv);
        var document = ConnectionCsvCodec.Parse(csv);
        Assert.False(Assert.Single(ConnectionCsvCodec.ReadEntries(document, document.SuggestedMapping)).SshUseAgent);
        var legacy = ConnectionXmlCodec.Read("<ConectaTI><Connection Name=\"Synthetic\" Host=\"host.example.test\" Protocol=\"Ssh\" SshPreference=\"OpenSsh\" SshUseAgent=\"false\"/></ConectaTI>");
        Assert.Equal(SshClientPreference.OpenSsh, Assert.Single(legacy).SshClient);
        Assert.False(legacy[0].SshUseAgent);
        var json = ConnectionJsonCodec.Write(legacy);
        Assert.Contains("\"SshClient\":", json); Assert.Contains("\"SshUseAgent\": false", json);
        Assert.True(legacy[0].HasSameEditableValues(Assert.Single(ConnectionJsonCodec.Read(json)!)));
    }

    private sealed class Lookup : ICredentialLookup
    {
        public Task<SavedCredential?> GetCredentialByIdAsync(string id) => Task.FromResult<SavedCredential?>(new() { Id = id, Name = "Synthetic" });
    }
    private sealed class Reader : ICredentialSecretReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ReadSecretAsync(SavedCredential credential) { Entered.TrySetResult(); return Completion.Task; }
    }
    private sealed class MemoryClipboard : ITextClipboard
    {
        public string? Text { get; private set; }
        public bool IsSecret { get; private set; }
        public int ClearSeconds { get; private set; }
        public Task<bool> CopyTextWithRetryAsync(string text, bool isSecret = false, int autoClearSeconds = 0, Func<bool>? canCopy = null)
        {
            if (canCopy != null && !canCopy()) return Task.FromResult(false);
            Text = text; IsSecret = isSecret; ClearSeconds = autoClearSeconds; return Task.FromResult(true);
        }
    }
    private sealed class PasswordEncoder : IRdpPasswordEncoder
    {
        public int Calls { get; private set; }
        public string Encode(string password) { Calls++; return "ABCDEF"; }
    }
    private sealed class ProcessRunner : IProcessRunner
    {
        public bool Fail { get; init; }
        public ProcessStartInfo? Captured { get; private set; }
        public int? Start(ProcessStartInfo info) { Captured = info; return Fail ? throw new IOException("Synthetic process failure") : 12345; }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(paths.RootDirectory, true); } catch { } }
}
