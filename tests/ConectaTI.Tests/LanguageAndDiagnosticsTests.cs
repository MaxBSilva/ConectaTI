using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace ConectaTI.Tests;

public sealed class LanguageAndDiagnosticsTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_Language_" + Guid.NewGuid().ToString("N")));
    public static string ProjectRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ConectaTI.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("Project root");
        }
    }
    [Theory]
    [InlineData("pt-PT", "pt-BR")] [InlineData("pt-AO", "pt-BR")]
    [InlineData("en-AU", "en")] [InlineData("en-GB", "en")]
    [InlineData("es-MX", "es")] [InlineData("es-AR", "es")]
    [InlineData("fr-FR", "pt-BR")] [InlineData("ja-JP", "pt-BR")]
    public void AutomaticLanguage_MapsWindowsRegionalVariants_AndFallsBack(string windows, string expected)
        => Assert.Equal(expected, LocalizationService.Resolve(AppLanguage.Automatic, CultureInfo.GetCultureInfo(windows)).Name);
    [Fact]
    public void Resources_AllLanguagesHaveMatchingKeysParametersAndEveryXamlResource()
    {
        var folder = Path.Combine(ProjectRoot, "src/ConectaTI.App/Localization");
        Dictionary<string, string> Read(string file) => XDocument.Load(Path.Combine(folder, file)).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var neutral = Read("Strings.resx");
        Assert.True(neutral.Count >= 580);
        var manager = new ResourceManager("ConectaTI.App.Localization.Strings", typeof(L).Assembly);
        foreach (var language in new[] { "en", "es" })
        {
            var translated = Read($"Strings.{language}.resx");
            Assert.Equal(neutral.Keys.Order(), translated.Keys.Order());
            foreach (var (key, original) in neutral)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]));
                string[] Parameters(string value) => Regex.Matches(value, @"\{(\d+)(?:[,}:])").Select(m => m.Groups[1].Value).Order().ToArray();
                Assert.Equal(Parameters(original), Parameters(translated[key]));
                Assert.Equal(translated[key], manager.GetString(key, CultureInfo.GetCultureInfo(language)));
            }
        }
        foreach (var path in Directory.GetFiles(Path.Combine(ProjectRoot, "src/ConectaTI.App"), "*.xaml", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"\{loc:Loc (Text[A-F0-9]+)\}")) Assert.True(neutral.ContainsKey(match.Groups[1].Value), path);
        LocalizationService.Current.SetLanguage(AppLanguage.English);
        Assert.Equal("No saved password", L.Text("Sem senha salva"));
        Assert.Equal("An absent resource", L.Text("An absent resource"));
        LocalizationService.Current.SetLanguage(AppLanguage.Spanish);
        Assert.Equal("Sin contraseña guardada", L.Text("Sem senha salva"));
    }
    [Fact]
    public async Task HotLanguageChange_PreservesSelectionFiltersDraftSecretAndStoredData()
    {
        var logger = new AppLogger(_paths); var settings = new SettingsService(_paths, logger); settings.Load();
        var repo = new SqliteRepository(_paths, logger); repo.InitializeAndMigrate();
        var entry = new ConnectionEntry { Name = "Salvar", ClientName = "Produção", GroupName = "Teste", Host = "srv.example.test", Username = "Salvar", Notes = "English Español Produção", Port = 33890 };
        await repo.SaveConnectionAsync(entry);
        var vault = new CredentialVaultService(settings, repo, logger, false); VaultTestSupport.Initialize(vault); var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });
        var import = new ImportExportService(repo, vault, logger);
        var vm = new MainViewModel(_paths, logger, settings, repo, vault, clipboard, new ConnectionLauncherService(_paths, settings, repo, vault, clipboard, logger), import, new NetworkDiagnosticService());
        await vm.InitializeAsync();
        vm.EditingEntry.Name = "Draft São João"; vm.InlinePasswordInput = "plaintext-pending-only";
        vm.SearchText = "Salvar"; vm.SelectedClientFilter = "Produção";
        var before = JsonSerializer.Serialize((await repo.GetAllConnectionsAsync()).Single());
        var selected = vm.SelectedConnection!.Id;
        var activeEntry = vm.EditingEntry;
        vm.StatusMessage = L.Format("Conexão '{0}' salva.", "Salvar");
        for (var i = 0; i < 2200; i++) L.Format("Ephemeral test {0}", i);
        var culture = CultureInfo.CurrentCulture;
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Spanish, AppLanguage.Portuguese, AppLanguage.Automatic })
        {
            LocalizationService.Current.SetLanguage(language);
            Assert.Same(activeEntry, vm.EditingEntry); Assert.Equal(selected, vm.SelectedConnection?.Id);
            Assert.Equal("Draft São João", vm.EditingEntry.Name); Assert.Equal("plaintext-pending-only", vm.InlinePasswordInput);
            Assert.Equal("Salvar", vm.SearchText); Assert.Equal("Produção", vm.SelectedClientFilter); Assert.True(vm.HasPendingChanges); Assert.False(vm.IsSaving);
            Assert.Equal(before, JsonSerializer.Serialize((await repo.GetAllConnectionsAsync()).Single()));
            Assert.Equal(culture, CultureInfo.CurrentCulture);
            if (language == AppLanguage.English) Assert.Equal("Connection 'Salvar' saved.", vm.StatusMessage);
            if (language == AppLanguage.Spanish) Assert.Equal("Conexión 'Salvar' guardada.", vm.StatusMessage);
            var path = Path.Combine(_paths.RootDirectory, language + ".csv");
            await import.ExportToCsvAsync(path, await repo.GetAllConnectionsAsync());
            Assert.Contains("Produção;Rdp;srv.example.test;33890;Salvar", await File.ReadAllTextAsync(path));
        }
        vm.ClearSessionDrafts();
    }
    [Fact]
    public async Task PasswordRecoveryFailure_IsDistinctFromMissing_AndPendingChangesAreAmber()
    {
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        var logger = new AppLogger(_paths); var settings = new SettingsService(_paths, logger); settings.Load();
        var repo = new SqliteRepository(_paths, logger); repo.InitializeAndMigrate();
        var credential = new SavedCredential { Name = "Unreadable synthetic", ProtectedSecretBase64 = "DPAPI1:invalid!" };
        await repo.SaveCredentialAsync(credential);
        await repo.SaveConnectionAsync(new() { Name = "Synthetic", Host = "example.test", ClientName = "Geral", CredentialId = credential.Id });
        var vault = new CredentialVaultService(settings, repo, logger, false); VaultTestSupport.Initialize(vault); var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });
        var vm = new MainViewModel(_paths, logger, settings, repo, vault, clipboard, new ConnectionLauncherService(_paths, settings, repo, vault, clipboard, logger), new ImportExportService(repo, vault, logger), new NetworkDiagnosticService());
        await vm.InitializeAsync(); await vm.TogglePasswordVisibilityAsync();
        Assert.True(vm.PasswordRecoveryFailed); Assert.True(vm.HasSavedPassword); Assert.False(vm.HasPendingChanges);
        Assert.Equal(CredentialVaultService.RecoveryMessage(new System.Security.Cryptography.CryptographicException()), vm.SavedPasswordBadgeText);
        Assert.Equal(System.Windows.Media.Brushes.IndianRed, vm.PasswordStateBrush);
        vm.InlinePasswordInput = "new-fictional-password";
        Assert.True(vm.HasPendingPasswordChange); Assert.Equal(System.Windows.Media.Brushes.DarkGoldenrod, vm.PasswordStateBrush);
        Assert.True(await vm.SaveConnectionAsync());
        Assert.False(vm.PasswordRecoveryFailed); Assert.Equal("Senha salva", vm.SavedPasswordBadgeText);
        Assert.Equal(System.Windows.Media.Brushes.SeaGreen, vm.PasswordStateBrush);
    }
    [Fact]
    public void Preferences_RestoreLayoutSafely_AndPersistLanguageAndDensity()
    {
        var logger = new AppLogger(_paths); var settings = new SettingsService(_paths, logger);
        Assert.Equal(AppLanguage.Automatic, settings.Load().Language);
        File.WriteAllText(_paths.SettingsPath, "{\"Language\":999,\"FontSize\":\"NaN\",\"LeftPaneWidth\":\"Infinity\",\"RightPaneWidth\":-5,\"ImportColumnWidths\":{\"0\":-3,\"1\":120,\"2\":\"NaN\"}}");
        var loaded = settings.Load();
        Assert.Equal(AppLanguage.Automatic, loaded.Language); Assert.Equal(12.5, loaded.FontSize);
        Assert.Equal(220, loaded.LeftPaneWidth); Assert.Equal(260, loaded.RightPaneWidth); Assert.Single(loaded.ImportColumnWidths);
        loaded.Language = AppLanguage.Spanish; loaded.ComfortableMode = true; loaded.FontSize = 16; loaded.LeftPaneWidth = 278;
        settings.Save(); var reopened = new SettingsService(_paths, logger).Load();
        Assert.Equal(AppLanguage.Spanish, reopened.Language); Assert.True(reopened.ComfortableMode); Assert.Equal(16, reopened.FontSize); Assert.Equal(278, reopened.LeftPaneWidth);
    }
    [Fact]
    public async Task Diagnostics_ContainTechnicalContextAndCurrentMemory_AndRedactSecretsWithBoundedLogs()
    {
        var logger = new AppLogger(_paths); var repo = new SqliteRepository(_paths, logger); repo.InitializeAndMigrate();
        var secret = "fictional-sensitive-value";
        using (logger.Redact([secret]))
        {
            try { throw new IOException($"Injected {secret} password=\"quoted sensitive password\" token=abc Bearer abc.def <Data><List>fullXML-secret</List></Data>"); }
            catch (Exception ex) { logger.RecordFailure("Import.Decoding", ex, 12345, 600, TimeSpan.FromSeconds(2)); }
        }
        LocalizationService.Current.SetLanguage(AppLanguage.Spanish);
        var info = await new AppDiagnosticService(_paths, repo, logger).CollectDiagnosticInfoAsync();
        Assert.Equal("Spanish", info.SelectedLanguage); Assert.Equal("es", info.EffectiveLanguage);
        Assert.True(info.ProcessWorkingSetBytes > 0); Assert.Equal(12345, info.Failure?.FileSize); Assert.Equal(600, info.Failure?.IdentifiedCount);
        Assert.Equal("Import.Decoding", info.Failure?.Stage); Assert.Equal(DateTimeKind.Utc, info.Failure?.AtUtc.Kind);
        var report = AppDiagnosticService.FormatReport(info);
        Assert.Contains("Working Set", report); Assert.Contains("IOException", report); Assert.Contains("Stack trace", report); Assert.Contains(_paths.DatabasePath, report);
        foreach (var value in new[] { secret, "quoted sensitive password", "abc.def", "fullXML-secret", "<Data>" }) Assert.DoesNotContain(value, report);
        for (var i = 0; i < 1300; i++) logger.Info("Bounded", new string('x', 1900));
        var logs = new DirectoryInfo(_paths.LogsDirectory).GetFiles("conectati-*.log*");
        Assert.InRange(logs.Length, 2, 12); Assert.All(logs, file => Assert.True(file.Length <= AppLogger.MaxLogBytes));
        Assert.DoesNotContain(secret, string.Join("\n", logs.Select(file => File.ReadAllText(file.FullName))));
    }
    public void Dispose()
    {
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.RootDirectory, true); } catch (IOException) { }
    }
}
