using System.IO;
using System.Text;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Security;
using Microsoft.Data.Sqlite;
namespace ConectaTI.Tests;

public sealed class RdpTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ConectaTI RDP tests " + Guid.NewGuid().ToString("N"));
    private readonly AppPaths paths;
    private readonly AppLogger logger;
    private readonly SqliteRepository repo;
    public RdpTests()
    {
        paths = new(root); logger = new(paths); repo = new(paths, logger); repo.InitializeAndMigrate();
    }
    // Same properties as the reported case, with fictional host and account.
    private const string Sample = """
        full address:s:APP02.example.test /admin
        username:s:EXAMPLE\operator
        gatewayhostname:s:gateway.example.test
        gatewayusagemethod:i:2
        gatewaycredentialssource:i:4
        gatewayprofileusagemethod:i:1
        promptcredentialonce:i:1
        authentication level:i:2
        prompt for credentials:i:0
        negotiate security layer:i:1
        enablerdsaadauth:i:0
        screen mode id:i:2
        use multimon:i:0
        desktopwidth:i:1920
        desktopheight:i:1080
        redirectclipboard:i:1
        redirectprinters:i:1
        redirectsmartcards:i:1
        redirectwebauthn:i:1
        drivestoredirect:s:
        """;
    [Fact]
    public void ReportedPropertiesRoundTripSemanticallyAndAdminIsSeparated()
    {
        var imported = RdpFileService.Import(Sample, "Test APP02");
        Assert.Equal("APP02.example.test", imported.Entry.Host);
        Assert.True(imported.Entry.RdpAdminSession);
        Assert.Contains(imported.Warnings, w => w.Contains("Normalização"));
        var generated = RdpFileService.Parse(RdpFileService.Build(imported.Entry));
        var expected = RdpFileService.Parse(Sample);
        expected["full address"] = new("s", "APP02.example.test");
        expected["username"] = new("s", "operator");
        expected["domain"] = new("s", "EXAMPLE");
        foreach (var property in expected) Assert.Equal(property.Value, generated[property.Key]);
        Assert.Equal("4", generated["gatewaycredentialssource"].Value);
        Assert.Equal("2", generated["gatewayusagemethod"].Value);
    }
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void DetectsEncodingsAndPathsWithSpaces(string encoding)
    {
        Encoding enc = encoding switch { "utf8bom" => new UTF8Encoding(true), "utf16le" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode, _ => new UTF8Encoding(false) };
        var file = Path.Combine(root, "connection with spaces.rdp");
        File.WriteAllText(file, Sample + "\nalternate shell:s:C:\\Program Files\\Example\\tool.exe\n", enc);
        var result = RdpFileService.ImportFile(file);
        Assert.Equal("gateway.example.test", result.Entry.RdpOptions.GatewayHostname);
        Assert.Contains(@"C:\Program Files\Example\tool.exe", result.Entry.RdpOptions.AdditionalProperties);
    }
    [Fact]
    public void PortsColonsAndUnknownPropertiesSurviveAndNoDuplicateKeys()
    {
        var entry = RdpFileService.Import("full address:s:app.example.test:3390\ncustom setting:s:C:\\a b\\x:y", "Test").Entry;
        Assert.Equal(3390, entry.Port);
        entry.RdpOptions.AdditionalProperties += "FULL ADDRESS:s:wrong.example.test\ngatewayusagemethod:i:1\n";
        entry.RdpOptions.GatewayHostname = "gateway.example.test";
        var content = RdpFileService.Build(entry);
        Assert.Contains("custom setting:s:C:\\a b\\x:y", content);
        Assert.DoesNotContain("wrong.example.test", content);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lines.Length, lines.Select(l => l.Split(':', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("gatewayusagemethod:i:2", content);
    }
    [Theory]
    [InlineData("host.example.test /admin /f")]
    [InlineData("host.example.test /f")]
    [InlineData("host.example.test & command")]
    public void DoesNotInterpretGenericAddressCommands(string address) =>
        Assert.Throws<FormatException>(() => RdpFileService.Import("full address:s:" + address, "Test"));

    [Fact]
    public async Task ReopenSqlitePreservesGatewayOriginalAdditionalAndCloneIsolation()
    {
        var entry = RdpFileService.Import(Sample, "Test").Entry;
        await repo.SaveConnectionAsync(entry);
        var reopened = new SqliteRepository(paths, logger); reopened.InitializeAndMigrate();
        var saved = (await reopened.GetConnectionByIdAsync(entry.Id))!;
        Assert.True(entry.HasSameEditableValues(saved));
        var clone = saved.Clone(); clone.RdpOptions.GatewayUsageMethod = 1;
        Assert.False(saved.HasSameEditableValues(clone)); Assert.Equal(2, saved.RdpOptions.GatewayUsageMethod);
        Assert.Contains("gatewayhostname", saved.RdpOptions.OriginalContent);
    }
    [Fact]
    public async Task OldDatabaseMigratesAndOldConnectionsDoNotGetGateway()
    {
        var entry = new ConnectionEntry { Name = "Legacy", Host = "old.example.test" };
        await repo.SaveConnectionAsync(entry);
        using (var conn = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            conn.Open(); using var command = conn.CreateCommand();
            command.CommandText = "ALTER TABLE Connections DROP COLUMN RdpOptionsJson; DELETE FROM SchemaMigrations WHERE Version>=9;";
            command.ExecuteNonQuery();
        }
        var reopened = new SqliteRepository(paths, logger); reopened.InitializeAndMigrate();
        var saved = (await reopened.GetConnectionByIdAsync(entry.Id))!;
        Assert.Equal(10, reopened.GetSchemaVersion());
        Assert.DoesNotContain("gatewayhostname", RdpFileService.Build(saved));
        Assert.True(saved.RdpFullScreen); Assert.Equal(3389, saved.Port);
    }
    [Fact]
    public void SecretsAndSignatureNeverEnterComparisonOrDiagnostic()
    {
        var entry = RdpFileService.Import(Sample + "\npassword:s:SYNTHETIC_SECRET\npassword 51:b:DEADBEEF\nsignature:s:SIGNED\nsignscope:s:scope\n", "Test");
        var diagnostic = RdpFileService.Build(entry.Entry);
        foreach (var text in new[] { diagnostic, entry.Entry.RdpOptions.OriginalContent, entry.Entry.RdpOptions.AdditionalProperties })
        {
            Assert.DoesNotContain("SYNTHETIC_SECRET", text); Assert.DoesNotContain("DEADBEEF", text);
            Assert.DoesNotContain("signature:", text); Assert.DoesNotContain("signscope:", text);
        }
        Assert.Contains(entry.Warnings, w => w.Contains("assinatura"));
    }
    [Fact]
    public void LaunchPlansUseUniquePathsSeparatedArgumentsAndNoPasswordRequired()
    {
        var settings = new SettingsService(paths, logger); settings.Load();
        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        var launcher = new ConnectionLauncherService(paths, settings, repo, vault, new ClipboardService(logger), logger);
        var entry = RdpFileService.Import(Sample, "Test").Entry;
        var first = launcher.BuildLaunchPlan(entry); var second = launcher.BuildLaunchPlan(entry);
        Assert.NotEqual(first.TempRdpFilePath, second.TempRdpFilePath);
        Assert.Equal(first.TempRdpFilePath, first.Arguments[0]); Assert.Equal("/admin", first.Arguments[1]);
        Assert.Contains("gatewaycredentialssource:i:4", first.TempRdpContent);
        Assert.DoesNotContain("password", first.TempRdpContent!);
    }
    [Theory]
    [InlineData("enablecredsspsupport:i:0")]
    [InlineData("authentication level:i:0")]
    [InlineData("negotiate security layer:i:0")]
    public void RejectsSecurityDowngrades(string property)
    {
        var entry = new ConnectionEntry { Host = "app.example.test" };
        entry.RdpOptions.AdditionalProperties = property;
        Assert.Throws<FormatException>(() => RdpFileService.Build(entry));
    }
    [Theory]
    [InlineData("csv")]
    [InlineData("xml")]
    [InlineData("json")]
    public async Task NativeExportsPreserveRdpConfiguration(string format)
    {
        var settings = new SettingsService(paths, logger); settings.Load();
        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        var service = new ImportExportService(repo, vault, logger);
        var entry = RdpFileService.Import(Sample, "Test").Entry;
        var file = Path.Combine(root, "export." + format);
        ImportPreviewResult preview;
        if (format == "json") { await service.ExportToJsonAsync(file, [entry]); preview = service.ParseJsonPreview(file, []); }
        else if (format == "xml") { await service.ExportToXmlAsync(file, [entry]); preview = service.ParseXmlPreview(file, []); }
        else
        {
            await service.ExportToCsvAsync(file, [entry]);
            var doc = service.ParseCsvFile(file); preview = service.BuildCsvPreview(doc, doc.SuggestedMapping, []);
        }
        var imported = Assert.Single(preview.Items).Entry;
        Assert.True(entry.RdpOptions.SameValues(imported.RdpOptions));
        Assert.Equal(entry.RdpAdminSession, imported.RdpAdminSession);
        Assert.Contains("gateway.example.test", File.ReadAllText(file));
    }
    [Fact]
    public async Task ImportDraftRequiresExplicitSaveAndKeepsExistingConnection()
    {
        var existing = new ConnectionEntry { Name = "Existing", Host = "APP02.example.test" };
        await repo.SaveConnectionAsync(existing);
        var settings = new SettingsService(paths, logger); settings.Load();
        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        var clipboard = new ClipboardService(logger);
        var vm = new ConectaTI.App.ViewModels.MainViewModel(paths, logger, settings, repo, vault, clipboard,
            new ConnectionLauncherService(paths, settings, repo, vault, clipboard, logger),
            new ImportExportService(repo, vault, logger), new ConectaTI.App.Services.Diagnostics.NetworkDiagnosticService());
        await vm.InitializeAsync(seedDemoIfEmpty: false);
        vm.ImportRdpDraft(RdpFileService.Import(Sample, "Imported"));
        Assert.True(vm.HasPendingChanges); Assert.True(vm.CanSave);
        Assert.NotEqual(existing.Id, vm.EditingEntry.Id);
        Assert.Single(await repo.GetAllConnectionsAsync());
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal(2, (await repo.GetAllConnectionsAsync()).Count);
        Assert.Equal("Existing", (await repo.GetConnectionByIdAsync(existing.Id))!.Name);
        vm.EditingEntry.RdpOptions.GatewayUsageMethod = 1;
        Assert.True(vm.HasPendingChanges);
        Assert.True(await vm.SaveConnectionAsync());
        Assert.Equal(1, (await repo.GetConnectionByIdAsync(vm.EditingEntry.Id))!.RdpOptions.GatewayUsageMethod);
    }

    [Fact]
    public async Task PasswordTransportStaysProtectedAndDiagnosticAndLogsExcludeSecret()
    {
        const string secret = "SYNTHETIC_RDP_PASSWORD_2026";
        var entry = RdpFileService.Import(Sample, "Test").Entry;
        var settings = new SettingsService(paths, logger); settings.Load();
        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        var persistence = new ConnectionPersistenceService(repo, vault, logger);
        var saved = await persistence.SaveAsync(entry, PasswordEditIntent.Replace, secret, "Synthetic");
        settings.Current.MstscExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
        string? fileContent = null;
        var launcher = new ConnectionLauncherService(paths, settings, repo, vault, new ClipboardService(logger), logger,
            psi => { fileContent = File.ReadAllText(psi.ArgumentList[0]); Assert.DoesNotContain(secret, string.Join(" ", psi.ArgumentList)); return Environment.ProcessId; });
        var result = await launcher.LaunchAsync(saved.Entry);
        Assert.True(result.Started); Assert.NotNull(fileContent); Assert.DoesNotContain(secret, fileContent);
        Assert.Contains("password 51:b:", fileContent);
        var diagnostic = RdpFileService.Sanitize(fileContent!);
        Assert.DoesNotContain("password", diagnostic); Assert.DoesNotContain(secret, diagnostic);
        foreach (var log in Directory.GetFiles(paths.LogsDirectory)) Assert.DoesNotContain(secret, File.ReadAllText(log));
    }
    [Fact]
    public void SessionNamesChangeOnlyTheFilePathAndKeepRdpAndAdminArguments()
    {
        var settings = new SettingsService(paths, logger); settings.Load();
        var vault = new CredentialVaultService(settings, repo, logger, useWindowsVault: false);
        var launcher = new ConnectionLauncherService(paths, settings, repo, vault, new ClipboardService(logger), logger);
        var firstEntry = RdpFileService.Import(Sample, "ERP fictício").Entry;
        var secondEntry = firstEntry.Clone(); secondEntry.Name = "HCM fictício";
        var first = launcher.BuildLaunchPlan(firstEntry); var second = launcher.BuildLaunchPlan(secondEntry);
        Assert.Equal(first.TempRdpContent, second.TempRdpContent);
        Assert.Equal(RdpFileService.Build(firstEntry), first.TempRdpContent);
        Assert.NotEqual(Path.GetFileName(first.TempRdpFilePath), Path.GetFileName(second.TempRdpFilePath));
        Assert.Equal("/admin", first.Arguments[1]); Assert.Equal("/admin", second.Arguments[1]);
        Assert.Equal(first.TempRdpFilePath, first.Arguments[0]); Assert.Equal(second.TempRdpFilePath, second.Arguments[0]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { }
    }
}



