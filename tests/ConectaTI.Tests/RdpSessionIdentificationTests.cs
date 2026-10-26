using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Launchers;

namespace ConectaTI.Tests;

public sealed class RdpSessionIdentificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ConectaTI_RdpNames_" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly SqliteRepository _repository;
    private readonly ConnectionLauncherService _launcher;
    private readonly ConcurrentQueue<ProcessStartInfo> _starts = new();

    public RdpSessionIdentificationTests()
    {
        _paths = new AppPaths(_root);
        var logger = new AppLogger(_paths);
        _settings = new SettingsService(_paths, logger);
        _settings.Current.MstscExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
        _settings.Current.RegisterRdpCredentialsInWindowsVault = false;
        _repository = new SqliteRepository(_paths, logger);
        _repository.InitializeAndMigrate();
        var vault = new CredentialVaultService(_settings, _repository, logger, useWindowsVault: false);
        var clipboard = new ClipboardService(logger, _ => true, () => null, () => { });
        _launcher = new ConnectionLauncherService(_paths, _settings, _repository, vault, clipboard, logger,
            customProcessStarter: psi => { _starts.Enqueue(psi); return null; });
    }

    [Theory]
    [InlineData("Cliente São José", "Cliente São José")]
    [InlineData("  Acesso com espaços  ", "Acesso com espaços")]
    [InlineData("Filial: São/Paulo\\TI*?<>|\"", "Filial_ São_Paulo_TI______")]
    [InlineData("..\\..\\pasta/acesso", ".._.._pasta_acesso")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("com¹", "_com¹")]
    [InlineData(" . . ", "Conexao")]
    [InlineData("Nome. ", "Nome")]
    [InlineData("A\r\nB", "A__B")]
    public void FileName_IsReadableAndSafe_WithoutChangingRegistration(string name, string expected)
    {
        var entry = Entry(name);
        var plan = _launcher.BuildLaunchPlan(entry);
        Assert.Equal(expected + ".rdp", Path.GetFileName(plan.TempRdpFilePath));
        Assert.Equal(name, entry.Name);
        Assert.DoesNotContain(entry.Id, Path.GetFileName(plan.TempRdpFilePath)!);
        Assert.Equal(_paths.TempRdpDirectory, Path.GetDirectoryName(Path.GetDirectoryName(plan.TempRdpFilePath)));
        Assert.True(Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(plan.TempRdpFilePath)), "N", out _));
    }

    [Fact]
    public void LongUnicodeName_TruncatesOnlyFileName_AtTextElementBoundary()
    {
        var entry = Entry(string.Concat(Enumerable.Repeat("Á😀e\u0301", 80)) + "final");
        var original = entry.Name;
        var stem = Path.GetFileNameWithoutExtension(_launcher.BuildLaunchPlan(entry).TempRdpFilePath)!;
        Assert.InRange(stem.Length, 1, 100);
        Assert.False(char.IsHighSurrogate(stem[^1]));
        Assert.StartsWith(stem, original);
        Assert.Equal(original, entry.Name);
    }

    [Fact]
    public void EmptyAccessName_UsesClientName_WithoutCredentialsOrEndpoint()
    {
        Assert.Equal("Cliente fictício.rdp", Path.GetFileName(_launcher.BuildLaunchPlan(Entry(" ")).TempRdpFilePath));
    }

    [Fact]
    public async Task ProcessExit_ReleasesLeaseAndRemovesOnlyItsOwnSessionDirectory()
    {
        var path = _launcher.BuildLaunchPlan(Entry("Processo encerrado")).TempRdpFilePath!;
        var other = _launcher.BuildLaunchPlan(Entry("Outra sessão")).TempRdpFilePath!;
        var lease = RdpSessionFiles.AcquireLease(path);
        using var otherLease = RdpSessionFiles.AcquireLease(other);
        await File.WriteAllTextAsync(path, "synthetic");
        await File.WriteAllTextAsync(other, "synthetic");
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("exit 0");
        var process = Process.Start(psi)!;
        await RdpSessionFiles.CleanupOnExitAsync(process, lease, path).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("192.0.2.11", 3389, "192.0.2.11")]
    [InlineData("servidor-nao-resolvivel.example.invalid", 3389, "servidor-nao-resolvivel.example.invalid")]
    [InlineData("servidor.example.invalid", 3391, "servidor.example.invalid:3391")]
    [InlineData("2001:db8::1", 3391, "[2001:db8::1]:3391")]
    public void Endpoint_IsKeptInRdpContent_NotAppendedToFileName(string host, int port, string fullAddress)
    {
        var entry = Entry("Cliente Árvore");
        entry.Host = host;
        entry.Port = port;
        var plan = _launcher.BuildLaunchPlan(entry);
        Assert.Equal("Cliente Árvore.rdp", Path.GetFileName(plan.TempRdpFilePath));
        Assert.Contains($"full address:s:{fullAddress}" + Environment.NewLine, plan.TempRdpContent);
        Assert.Contains($"server port:i:{port}" + Environment.NewLine, plan.TempRdpContent);
        Assert.Equal(host, entry.Host);
    }

    [Fact]
    public void NamesAndIds_CannotChangeConnectionParametersOrExposeCredentials()
    {
        var entry = Entry("Cliente & Acesso");
        entry.Username = "operador.ficticio";
        entry.Domain = "EXEMPLO";
        entry.RdpFullScreen = false;
        entry.RdpWidth = 1440;
        entry.RdpHeight = 900;
        entry.RdpUseMultimon = true;
        entry.RdpAdminSession = true;
        var first = _launcher.BuildLaunchPlan(entry, null, "SegredoFicticio#123");
        entry.Name = "Outro nome";
        entry.ClientName = "Outro cliente";
        entry.Id = "../../id-nao-confiavel";
        var second = _launcher.BuildLaunchPlan(entry, null, "SegredoFicticio#123");
        // DPAPI gera ciphertext aleatório; os demais parâmetros devem ser idênticos.
        static string WithoutPassword(string content) => string.Join('\n', content.Split('\n').Where(line => !line.StartsWith("password 51:b:")));
        Assert.Equal(WithoutPassword(first.TempRdpContent!), WithoutPassword(second.TempRdpContent!));
        Assert.Contains("password 51:b:", second.TempRdpContent);
        Assert.Equal(new[] { second.TempRdpFilePath!, "/admin" }, second.Arguments);
        Assert.DoesNotContain("SegredoFicticio", second.TempRdpContent);
        Assert.DoesNotContain(entry.Username, second.TempRdpFilePath);
        Assert.DoesNotContain("SegredoFicticio", second.TempRdpFilePath);
        Assert.DoesNotContain("id-nao-confiavel", second.TempRdpFilePath);
    }

    [Fact]
    public async Task ConcurrentLaunches_DifferentClientsSameAccessAndSameNames_KeepSeparateFiles()
    {
        var first = Entry("Cliente Alfa");
        var second = Entry("Cliente Beta");
        var sameName = Entry(first.Name);
        var entries = new[] { first, second, first, sameName };
        foreach (var entry in entries.DistinctBy(entry => entry.Id))
            await _repository.SaveConnectionAsync(entry);

        var results = await Task.WhenAll(entries.Select(entry => _launcher.LaunchAsync(entry)));
        Assert.All(results, result => Assert.True(result.Started, result.ErrorMessage));
        Assert.Equal(4, results.Select(result => result.GeneratedRdpFilePath).Distinct().Count());
        Assert.Equal(4, results.Select(result => Path.GetDirectoryName(result.GeneratedRdpFilePath)).Distinct().Count());
        Assert.Equal(4, _starts.Count);
        for (var i = 0; i < entries.Length; i++)
        {
            var path = results[i].GeneratedRdpFilePath!;
            Assert.Equal(entries[i].Name + ".rdp", Path.GetFileName(path));
            Assert.True(File.Exists(path));
            Assert.Contains("full address:s:servidor-ficticio.example.invalid", await File.ReadAllTextAsync(path));
            Assert.Contains(_starts, psi => !psi.UseShellExecute && psi.ArgumentList.SequenceEqual(new[] { path }));
        }
    }

    [Fact]
    public void Cleanup_ProtectsActiveSessions_AndCollectsOnlyOldSessionFoldersAndLegacyFiles()
    {
        var active = _launcher.BuildLaunchPlan(Entry("Ativa")).TempRdpFilePath!;
        using var lease = RdpSessionFiles.AcquireLease(active);
        File.WriteAllText(active, "full address:s:192.0.2.1");
        Age(active);

        var abandoned = _launcher.BuildLaunchPlan(Entry("Abandonada")).TempRdpFilePath!;
        using (RdpSessionFiles.AcquireLease(abandoned)) { }
        File.WriteAllText(abandoned, "full address:s:192.0.2.2");
        Age(abandoned);
        var legacy = Path.Combine(_paths.TempRdpDirectory, "conectati-legado.rdp");
        File.WriteAllText(legacy, "synthetic");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddHours(-1));
        var unrelated = Path.Combine(_paths.TempRdpDirectory, "nao-e-sessao");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "preservar.rdp"), "synthetic");

        _paths.CleanupTempRdpFiles();
        Assert.True(File.Exists(active));
        Assert.False(Directory.Exists(Path.GetDirectoryName(abandoned)));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(unrelated, "preservar.rdp")));

        lease.Dispose();
        _paths.CleanupTempRdpFiles();
        Assert.False(Directory.Exists(Path.GetDirectoryName(active)));
    }

    private static void Age(string file)
    {
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(file)!, DateTime.UtcNow.AddHours(-1));
    }

    private static ConnectionEntry Entry(string name) => new()
    {
        Name = name, ClientName = "Cliente fictício", Protocol = ConnectionProtocol.Rdp,
        Host = "servidor-ficticio.example.invalid", Port = 3389
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
