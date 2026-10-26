using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ConectaTI.App.Data;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ConectaTI.Tests;

public sealed class ImportAndBatchTests(ITestOutputHelper output)
{
    private sealed class ImmediateProgress(Action<ImportProgress> action) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => action(value); }
    private sealed class Vault(SettingsService settings, SqliteRepository repository, AppLogger logger)
        : CredentialVaultService(settings, repository, logger, useWindowsVault: false)
    {
        public bool FailWrite, FailDelete;
        public int? NativeFailureAtWrite;
        public int WriteCalls;
        public readonly HashSet<string> Native = new();
        public override void WriteToWindowsVault(SavedCredential credential)
        {
            if (NativeFailureAtWrite == ++WriteCalls)
                WindowsCredentialVaultInterop.WriteGenericSecret("ConectaTI:Test:never-written",
                    credential.EffectiveUserDisplay, credential.ProtectedSecretBase64,
                    (ref WindowsCredentialVaultInterop.CREDENTIAL nativeCredential, uint flags) =>
                    { Marshal.SetLastPInvokeError(8); return false; });
            if (FailWrite) throw new IOException("injected vault failure");
            Native.Add(credential.Id);
        }
        public override void RemoveFromWindowsVault(string id)
        { if (FailDelete) throw new IOException("injected cleanup failure"); Native.Remove(id); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly AppPaths Paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Import_" + Guid.NewGuid().ToString("N")));
        public readonly AppLogger Logger;
        public readonly SettingsService Settings;
        public readonly SqliteRepository Repository;
        public readonly Vault Vault;
        public readonly ImportExportService Import;
        public Fixture()
        {
            LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
            Logger = new(Paths); Settings = new(Paths, Logger); Settings.Load();
            Repository = new(Paths, Logger); Repository.InitializeAndMigrate();
            Vault = new(Settings, Repository, Logger); VaultTestSupport.Initialize(Vault); Import = new(Repository, Vault, Logger);
        }
        public MainViewModel Vm()
        {
            var clipboard = new ClipboardService(Logger, _ => true, () => null, () => { });
            return new(Paths, Logger, Settings, Repository, Vault, clipboard,
                new ConnectionLauncherService(Paths, Settings, Repository, Vault, clipboard, Logger), Import, new NetworkDiagnosticService());
        }
        public void Execute(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Paths.DatabasePath);
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Integrity()
        {
            using var connection = new SqliteConnection("Data Source=" + Paths.DatabasePath);
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", command.ExecuteScalar());
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Paths.RootDirectory, true); } catch (IOException) { }
        }
    }
    private static ConnectionEntry Entry(int index) => new()
    { Id = "synthetic-" + index, Name = $"Acesso fictício São João & revisão {index:D4}", Host = $"srv-{index}.example.test", ClientName = "Geral", Username = "usuário.teste", Domain = "FICTICIO" };
    private static ImportPreviewResult Preview(ImportExportService service, IReadOnlyList<ConnectionEntry> entries, string? secret = null,
        IReadOnlyList<ConnectionEntry>? existing = null)
    {
        var result = service.BuildPreviewFromCandidates(entries, existing ?? [], "Synthetic");
        return new() { Items = result.Items.Select(i => new ImportPreviewItem
        { Entry = i.Entry, RowNumber = i.RowNumber, IsValid = i.IsValid, IsDuplicate = i.IsDuplicate,
            ExistingConnectionId = i.ExistingConnectionId, IsSelected = i.IsValid, RecoveredPlainPassword = secret }).ToList() };
    }
    private static string SyntheticXml(int count, bool threeInvalid = false)
    {
        // Native legacy AutoPuTTY envelope and fields, generated only from fictional records.
        var xml = XDocument.Parse(AutoPuttyCodec.GenerateCompatibleAutoPuttyXml(Enumerable.Range(0, count).Select(Entry)));
        var list = xml.Descendants("List").Single();
        var inner = XDocument.Parse("<List>" + AutoPuttyCodec.DecryptTripleDes(list.Value, AutoPuttyCodec.DefaultInternalCryptoKey) + "</List>");
        if (threeInvalid)
            foreach (var server in inner.Descendants("Server").Where((_, index) => index is 0 or 74 or 149))
                server.Element("Host")!.Value = AutoPuttyCodec.EncryptTripleDes("", AutoPuttyCodec.DefaultInternalCryptoKey);
        foreach (var node in inner.Descendants("Password"))
            node.Value = AutoPuttyCodec.EncryptTripleDes("Synthetic#SóTeste2026", AutoPuttyCodec.DefaultInternalCryptoKey);
        list.Value = AutoPuttyCodec.EncryptTripleDes(string.Concat(inner.Root!.Elements()), AutoPuttyCodec.DefaultInternalCryptoKey);
        return xml.ToString();
    }
    [Theory]
    [InlineData(1)] [InlineData(225)] [InlineData(600)] [InlineData(633)] [InlineData(1000)]
    public async Task NativeSyntheticAutoPutty_ImportMeasureAndReopen(int count)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Paths.RootDirectory, "synthetic-autoputty.xml");
        await File.WriteAllTextAsync(path, SyntheticXml(count));
        using var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;
        long peak = before;
        using var sampler = new Timer(_ => { try { process.Refresh(); InterlockedExtensions.Max(ref peak, process.WorkingSet64); } catch (InvalidOperationException) { } }, null, 0, 10);
        var clock = Stopwatch.StartNew();
        var preview = await fixture.Import.ReadPreviewAsync(path, []);
        var previewMs = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(count, preview.Items.Count);
        Assert.All(preview.Items, i => { Assert.True(i.IsValid); Assert.Equal("Synthetic#SóTeste2026", i.RecoveredPlainPassword); Assert.Contains("São João", i.Entry.Name); });
        var report = await fixture.Import.CommitImportAsync(preview, DuplicateImportHandling.Ignore);
        var totalMs = clock.Elapsed.TotalMilliseconds;
        process.Refresh();
        Assert.Equal(count, report.InsertedCount); Assert.Equal(count, report.FoundCount);
        Assert.Equal(0, report.UpdatedCount + report.IgnoredCount + report.InvalidSkippedCount);
        var duplicateReport = await fixture.Import.CommitImportAsync(preview, DuplicateImportHandling.Ignore);
        Assert.Equal(count, duplicateReport.IgnoredCount); Assert.Equal(0, duplicateReport.CredentialsImportedCount);
        var reopened = new SqliteRepository(fixture.Paths, fixture.Logger);
        reopened.InitializeAndMigrate();
        var saved = await reopened.GetAllConnectionsAsync();
        Assert.Equal(count, saved.Count); Assert.Equal(count, (await reopened.GetAllCredentialsAsync()).Count);
        Assert.All(saved, e => Assert.NotNull(e.CredentialId));
        var credential = await reopened.GetCredentialByIdAsync(saved[0].CredentialId!);
        Assert.Equal("Synthetic#SóTeste2026", fixture.Vault.UnprotectSecret(credential!));
        fixture.Integrity();
        var metrics = new { count, format = "synthetic native legacy AutoPuTTY XML (encrypted List + fields)", previewMs, totalMs,
            beforeWorkingSetBytes = before, afterWorkingSetBytes = process.WorkingSet64, sampledPeakWorkingSetBytes = peak,
            processPeakWorkingSetBytes = process.PeakWorkingSet64, fileBytes = new FileInfo(path).Length,
            operatingSystem = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
            machine = "local Windows test process; not the user's real import", atUtc = DateTime.UtcNow };
        var serialized = JsonSerializer.Serialize(metrics);
        output.WriteLine(serialized);
        var metricsPath = Environment.GetEnvironmentVariable("CONECTATI_IMPORT_METRICS");
        if (!string.IsNullOrEmpty(metricsPath)) await File.AppendAllTextAsync(metricsPath, serialized + Environment.NewLine);
    }
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Preview225_Valid222_Recovered225_CommitsWithoutNativeWrites(int nativeFailurePosition)
    {
        using var f = new Fixture();
        var path=Path.Combine(f.Paths.RootDirectory,"synthetic-225.xml");
        await File.WriteAllTextAsync(path,SyntheticXml(225,threeInvalid:true));
        var preview=await f.Import.ReadPreviewAsync(path,[]);
        Assert.Equal(225,preview.Items.Count); Assert.Equal(222,preview.ValidCount);
        Assert.Equal(225,preview.Items.Count(i=>!string.IsNullOrEmpty(i.RecoveredPlainPassword)));
        f.Vault.NativeFailureAtWrite=nativeFailurePosition; f.Vault.FailWrite=true; f.Vault.FailDelete=true;
        var report=await f.Import.CommitImportAsync(preview,DuplicateImportHandling.Ignore);
        Assert.Equal(222,report.InsertedCount); Assert.Equal(3,report.InvalidSkippedCount);
        Assert.Equal(0,f.Vault.WriteCalls); Assert.Empty(f.Vault.Native);
        var credentials=await f.Repository.GetAllCredentialsAsync();
        Assert.Equal(222,credentials.Count);
        Assert.All(credentials,c=>Assert.Equal(CredentialSecretStorage.SqliteDpapi,c.SecretStorage));
        f.Vault.Lock();
        foreach(var credential in credentials) Assert.Equal("Synthetic#SóTeste2026",await f.Vault.ReadSecretAsync(credential));
        f.Integrity();
    }

    [Fact]
    public async Task DuplicateDecisions_PreserveIdsSecretsAndActualCounts()
    {
        using var f = new Fixture(); var original = Entry(1);
        await f.Import.CommitImportAsync(Preview(f.Import, [original], "old-secret"), DuplicateImportHandling.Ignore);
        var old = (await f.Repository.GetAllConnectionsAsync()).Single(); var credentialId = old.CredentialId;
        var incoming = old.Clone(); incoming.Id = Guid.NewGuid().ToString(); incoming.Notes = "new note"; incoming.CredentialId = null;
        var report = await f.Import.CommitImportAsync(Preview(f.Import, [incoming], existing: [old]), DuplicateImportHandling.Update);
        Assert.Equal(1, report.UpdatedCount); Assert.Equal(0, report.InsertedCount);
        var updated = (await f.Repository.GetAllConnectionsAsync()).Single();
        Assert.Equal(old.Id, updated.Id); Assert.Equal(credentialId, updated.CredentialId); Assert.Equal("new note", updated.Notes);
        report = await f.Import.CommitImportAsync(Preview(f.Import, [incoming], "ignored-secret", [old]), DuplicateImportHandling.Ignore);
        Assert.Equal(1, report.IgnoredCount); Assert.Single(await f.Repository.GetAllCredentialsAsync());
        report = await f.Import.CommitImportAsync(Preview(f.Import, [incoming, incoming.Clone()], "new-secret", [old]), DuplicateImportHandling.CreateNew);
        Assert.Equal(2, report.InsertedCount); Assert.Equal(2, report.DuplicatedCount);
        Assert.Equal(3, (await f.Repository.GetAllConnectionsAsync()).Select(e => e.Id).Distinct().Count());
        Assert.Equal(3, (await f.Repository.GetAllCredentialsAsync()).Count);
        using var other = new Fixture();
        report = await other.Import.CommitImportAsync(Preview(other.Import, [incoming, incoming.Clone()], "replacement"), DuplicateImportHandling.Update);
        Assert.Equal(1, report.InsertedCount); Assert.Equal(0, report.UpdatedCount); Assert.Equal(1, report.IgnoredCount);
        Assert.Single(await other.Repository.GetAllConnectionsAsync()); Assert.Single(await other.Repository.GetAllCredentialsAsync());
    }
    [Fact]
    public async Task ImportReplacement_PreservesOldCredentialReferencedByTrash()
    {
        using var f = new Fixture();
        await f.Import.CommitImportAsync(Preview(f.Import, [Entry(0)], "old-shared"), DuplicateImportHandling.Ignore);
        var original = (await f.Repository.GetAllConnectionsAsync()).Single();
        var deleted = Entry(1); deleted.CredentialId = original.CredentialId; deleted.IsDeleted = true;
        await f.Repository.SaveConnectionAsync(deleted);
        await f.Import.CommitImportAsync(Preview(f.Import, [original.Clone()], "new-dedicated"), DuplicateImportHandling.Update);
        var current = (await f.Repository.GetConnectionByIdAsync(original.Id))!;
        Assert.NotEqual(original.CredentialId, current.CredentialId);
        Assert.Equal("old-shared", f.Vault.UnprotectSecret((await f.Repository.GetCredentialByIdAsync(original.CredentialId!))!));
        Assert.Equal("new-dedicated", f.Vault.UnprotectSecret((await f.Repository.GetCredentialByIdAsync(current.CredentialId!))!));
        Assert.Empty(await f.Repository.GetPendingVaultCleanupAsync());
    }
    [Fact]
    public async Task ModernAutoPuttyArgon2Aes_UsesCompatibleParameters_AndCanCancel()
    {
        using var f = new Fixture();
        var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var derivation = new Konscious.Security.Cryptography.Argon2id(System.Text.Encoding.UTF8.GetBytes(AutoPuttyCodec.DefaultInternalCryptoKey))
        { Salt = salt, DegreeOfParallelism = 16, Iterations = 4, MemorySize = 64 * 1024 };
        var key = derivation.GetBytes(32);
        string Encrypt(string plain)
        {
            using var aes = Aes.Create(); aes.Key = key; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor(); var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
            return "$v2$" + Convert.ToBase64String(salt.Concat(aes.IV).Concat(encryptor.TransformFinalBlock(bytes, 0, bytes.Length)).ToArray());
        }
        try
        {
            var host = Encrypt("example.test:3390"); var password = Encrypt("Senha sintética & ação");
            var xml = new XDocument(new XElement("Data", new XElement("List", Enumerable.Range(0, 2).Select(i =>
                new XElement("Server", new XAttribute("Name", "Modern synthetic " + i), new XElement("Host", host), new XElement("Password", password), new XElement("Type", "1"))))));
            var path = Path.Combine(f.Paths.RootDirectory, "modern.xml"); await File.WriteAllTextAsync(path, xml.ToString());
            Assert.False(AutoPuttyCodec.RequiresPassword(xml.ToString()));
            var result = await f.Import.ReadPreviewAsync(path, []);
            Assert.Equal(2, result.ValidCount);
            Assert.All(result.Items, i => { Assert.Equal("example.test", i.Entry.Host); Assert.Equal(3390, i.Entry.Port); Assert.Equal("Senha sintética & ação", i.RecoveredPlainPassword); });
            // Another read must work after the previous read's cached keys were cleared.
            Assert.Equal(2, (await f.Import.ReadPreviewAsync(path, [])).ValidCount);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var scope = AutoPuttyCodec.BeginReadScope(cancellation.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => AutoPuttyCodec.DecryptAutoPuttyString(host));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    [Theory]
    [InlineData("Importing")] [InlineData("Committing")]
    public async Task CancelBeforeCommit_RollsBackAllDataAndPreparedVault(string cancelStage)
    {
        using var f = new Fixture(); var original = Entry(-1); await f.Repository.SaveConnectionAsync(original);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(p => { if (p.Stage == cancelStage) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Import.CommitImportAsync(
            Preview(f.Import, Enumerable.Range(0, 80).Select(Entry).ToArray(), "temporary"), DuplicateImportHandling.Ignore, token: cancellation.Token, progress: progress));
        Assert.Equal(original.Id, (await f.Repository.GetAllConnectionsAsync()).Single().Id);
        Assert.Empty(await f.Repository.GetAllCredentialsAsync()); Assert.Empty(f.Vault.Native);
        Assert.Empty(await f.Repository.GetPendingVaultCleanupAsync()); f.Integrity();
    }
    [Fact]
    public async Task CancelAfterCommit_ReportsSuccess_AndCleanupFailureCanRecover()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var report = await f.Import.CommitImportAsync(Preview(f.Import, [Entry(0)], "saved"), DuplicateImportHandling.Ignore,
            token: cancellation.Token, progress: new ImmediateProgress(p => { if (p.Stage == "Completed") cancellation.Cancel(); }));
        Assert.Equal("Completed", report.Outcome); Assert.Single(await f.Repository.GetAllConnectionsAsync());
        f.Vault.FailWrite=true; f.Vault.FailDelete=true;
        await f.Import.CommitImportAsync(Preview(f.Import,[Entry(1)],"prepared"),DuplicateImportHandling.Ignore);
        Assert.Equal(2,(await f.Repository.GetAllConnectionsAsync()).Count);
        Assert.Empty(await f.Repository.GetPendingVaultCleanupAsync());
        Assert.Equal(0,f.Vault.WriteCalls);

    }
    [Fact]
    public async Task SqliteFault_RollsBackCredentialsAndConnections_PreservesSharedSecret()
    {
        using var f = new Fixture();
        await f.Import.CommitImportAsync(Preview(f.Import, [Entry(0)], "original"), DuplicateImportHandling.Ignore);
        var before = (await f.Repository.GetAllConnectionsAsync()).Single();
        f.Execute("CREATE TRIGGER fail_import BEFORE INSERT ON Connections WHEN NEW.Id='synthetic-2' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Import.CommitImportAsync(Preview(f.Import, [Entry(1), Entry(2)], "new"), DuplicateImportHandling.Ignore));
        Assert.Single(await f.Repository.GetAllConnectionsAsync()); Assert.Single(await f.Repository.GetAllCredentialsAsync());
        Assert.Equal("original", f.Vault.UnprotectSecret((await f.Repository.GetCredentialByIdAsync(before.CredentialId!))!));
        Assert.Empty(f.Vault.Native); f.Integrity();
        Assert.Equal("Import.SQLite.UpsertConnection", f.Logger.LastFailure?.Stage);
        Assert.Equal(2, f.Logger.LastFailure?.RecordIndex);
        Assert.Null(f.Logger.LastFailure?.NativeErrorCode);
    }
    [Fact]
    public async Task MalformedMissingOptionalAndUnreadableCredentials_AreSafeAndBounded()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Paths.RootDirectory, "input.xml");
        await File.WriteAllTextAsync(path, "<List><Server Name='Acesso &amp; Ação'><Host>example.test</Host></Server><Server Name='Bad'><Host> </Host></Server></List>");
        var preview = await f.Import.ReadPreviewAsync(path, []);
        Assert.Equal(1, preview.ValidCount); Assert.Equal(1, preview.InvalidCount);
        Assert.Empty(preview.Items[0].UnrecoveredFields); Assert.False(AutoPuttyCodec.RequiresPassword(await File.ReadAllTextAsync(path)));
        await File.WriteAllTextAsync(path, "<Data><List><Server Name='A'><Host>example.test</Host><Password>$v2$invalid!</Password></Server></List></Data>");
        preview = await f.Import.ReadPreviewAsync(path, []);
        Assert.Null(preview.Items[0].RecoveredPlainPassword is "" ? null : preview.Items[0].RecoveredPlainPassword);
        Assert.NotEmpty(preview.Items[0].UnrecoveredFields);
        Assert.False(AutoPuttyCodec.RequiresPassword(await File.ReadAllTextAsync(path)));
        await File.WriteAllTextAsync(path, "<Data><List>");
        preview = await f.Import.ReadPreviewAsync(path, []); Assert.Empty(preview.Items); Assert.NotNull(f.Logger.LastFailure);
        await File.WriteAllTextAsync(path, "<!DOCTYPE x [<!ENTITY attack 'secret'>]><Data><List>&attack;</List></Data>");
        preview = await f.Import.ReadPreviewAsync(path, []); Assert.Empty(preview.Items);
        await File.WriteAllTextAsync(path, "<Data><List>not-decryptable</List></Data>");
        await Assert.ThrowsAsync<CryptographicException>(() => f.Import.ReadPreviewAsync(path, []));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Task.Run(() => f.Import.BuildPreviewFromCandidates([new ConnectionEntry { Notes = new string('a', ImportLimits.MaxFieldCharacters + 1) }], [], "Synthetic")));
        Assert.Throws<InvalidDataException>(() => ImportLimits.CheckCount(20_001));
        Assert.Throws<InvalidDataException>(() => ImportLimits.ReadXml(string.Concat(Enumerable.Repeat("<Nested>", 70)) + string.Concat(Enumerable.Repeat("</Nested>", 70))));
        Assert.Throws<InvalidDataException>(() => f.Import.ParseCsvContent("Name;Host\nA;" + new string('x', ImportLimits.MaxFieldCharacters + 2)));
        Assert.Empty(await f.Repository.GetAllConnectionsAsync());
    }
    [Fact]
    public async Task PreviewRunsOffDispatcher_AndSecondImportIsRejected()
        => await StaTest.RunAsync(async () =>
        {
            using var f = new Fixture(); var path = Path.Combine(f.Paths.RootDirectory, "input.xml");
            await File.WriteAllTextAsync(path, SyntheticXml(1000));
            var gate = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var task = f.Import.ReadPreviewAsync(path, [], token: cancellation.Token, progress: new ImmediateProgress(p =>
            { if (p.Stage == "Decoding" && p.Completed == 0) { entered.TrySetResult(); gate.Wait(TimeSpan.FromSeconds(10)); } }));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var ticked = false; await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => ticked = true);
                Assert.True(ticked);
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Import.ReadPreviewAsync(path, []));
                cancellation.Cancel();
            }
            finally { gate.Set(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Empty(await f.Repository.GetAllConnectionsAsync());
        });
    [Fact]
    public async Task BatchEditChangesOnlyMarkedFields_AndSqliteFailureIsAtomic()
    {
        using var f = new Fixture();
        await f.Import.CommitImportAsync(Preview(f.Import, [Entry(0), Entry(1), Entry(2)], "shared-test"), DuplicateImportHandling.Ignore);
        var before = await f.Repository.GetAllConnectionsAsync();
        var vm = f.Vm(); await vm.InitializeAsync(); vm.EditingEntry.Notes = "pending notes"; vm.InlinePasswordInput = "pending secret";
        var selected = vm.SelectedConnection!.Id; vm.SearchText = "fictício";
        var count = await vm.ApplyBatchEditAsync([before[0].Id, before[1].Id], new(true, "Novo cliente", true, "", true, EnvironmentType.Staging));
        Assert.Equal(2, count); Assert.Equal(selected, vm.SelectedConnection!.Id); Assert.Equal("fictício", vm.SearchText);
        Assert.Equal("pending notes", vm.EditingEntry.Notes); Assert.Equal("pending secret", vm.InlinePasswordInput); Assert.True(vm.HasPendingChanges);
        var after = await f.Repository.GetAllConnectionsAsync();
        foreach (var row in after)
        {
            var original = before.Single(e => e.Id == row.Id);
            Assert.Equal(original.CredentialId, row.CredentialId); Assert.Equal(original.Name, row.Name); Assert.Equal(original.Host, row.Host); Assert.Equal(original.Username, row.Username); Assert.Equal(original.Port, row.Port);
            if (row.Id != before[2].Id) { Assert.Equal("", row.GroupName); Assert.Equal("Novo cliente", row.ClientName); Assert.Equal(EnvironmentType.Staging, row.Environment); }
            else Assert.True(original.HasSameEditableValues(row));
        }
        f.Execute("CREATE TRIGGER fail_batch BEFORE UPDATE ON Connections WHEN OLD.Id='synthetic-1' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Repository.ApplyBatchEditAsync([before[0].Id, before[1].Id], new(true, "Must rollback", false, "", false, EnvironmentType.Testing)));
        Assert.All((await f.Repository.GetAllConnectionsAsync()).Where(e => e.Id != before[2].Id), e => Assert.Equal("Novo cliente", e.ClientName)); f.Integrity();
        f.Execute("DROP TRIGGER fail_batch");
        await vm.ApplyBatchEditAsync([selected], new(true, "", false, "", false, EnvironmentType.Testing));
        vm.EditingEntry.Notes = "later manual edit";
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal("", (await f.Repository.GetConnectionByIdAsync(selected))!.ClientName);
        Assert.Equal("", (await f.Repository.GetConnectionByIdAsync(selected))!.GroupName);
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref long location, long value)
    { long current; do { current = Interlocked.Read(ref location); if (current >= value) return; } while (Interlocked.CompareExchange(ref location, value, current) != current); }
}
