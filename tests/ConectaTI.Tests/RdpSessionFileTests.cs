using System.IO;
using ConectaTI.App.Models;
using ConectaTI.App.Services;

namespace ConectaTI.Tests;

public class RdpSessionFileTests
{
    [Theory]
    [InlineData("192.0.2.10", 3389, "192.0.2.10꞉3389")]
    [InlineData("server.example.test", 3390, "server.example.test꞉3390")]
    [InlineData("server.example.test:3390", 3390, "server.example.test꞉3390")]
    [InlineData("[2001:db8::1]:3390", 3390, "[2001꞉db8꞉꞉1]꞉3390")]
    public void NameAndEndpointAreReadableWithoutDuplicatePort(string host, int port, string expected)
    {
        var path = RdpSessionFile.CreatePath(Path.GetTempPath(), new() { Name = "Avícola ERP Produção", Host = host, Port = port });
        Assert.Equal($"Avícola ERP Produção - {expected}.rdp", Path.GetFileName(path));
        Assert.DoesNotContain("conectati-", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("../../App: \"ERP\" <prod>|?*\\")]
    [InlineData("CON")]
    [InlineData("...   ")]
    [InlineData("Controle\r\n\t\u202eNome")]
    public void FileNameIsSafeAndStaysInsideOwnedDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "ConectaTI title tests");
        var path = RdpSessionFile.CreatePath(root, new() { Name = name, Host = "server.example.test" });
        Assert.Equal(Path.GetFullPath(root), Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName);
        Assert.DoesNotContain(Path.GetFileName(path), c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c));
        Assert.EndsWith("server.example.test꞉3389.rdp", path);
    }

    [Fact]
    public void ConcurrentAndIdenticalLaunchesUseDistinctDirectoriesAndDoNotExposeSecrets()
    {
        var entry = new ConnectionEntry { Id = "INTERNAL_TEST_ID", Name = "Operação ERP", Host = "server.example.test", Username = "PRIVATE_USER", CredentialId = "PRIVATE_CREDENTIAL" };
        var paths = Enumerable.Range(0, 100).AsParallel().Select(_ => RdpSessionFile.CreatePath(Path.GetTempPath(), entry)).ToArray();
        Assert.Equal(100, paths.Distinct().Count());
        Assert.Single(paths.Select(Path.GetFileName).Distinct());
        foreach (var path in paths)
        {
            Assert.DoesNotContain(entry.Id, path); Assert.DoesNotContain(entry.Username, path); Assert.DoesNotContain(entry.CredentialId, path);
        }
        var other = entry.Clone(); other.Name = "Outra operação ERP";
        Assert.NotEqual(Path.GetFileName(paths[0]), Path.GetFileName(RdpSessionFile.CreatePath(Path.GetTempPath(), other)));
    }

    [Fact]
    public void LongNamesAreBoundedWhileRetainingTheEndpointAndUnicodeCharacters()
    {
        var path = RdpSessionFile.CreatePath(Path.Combine(Path.GetTempPath(), "title tests with spaces"), new() { Name = string.Concat(Enumerable.Repeat("😀Avícola", 80)), Host = "server.example.test" });
        Assert.True(path.Length <= 250); Assert.EndsWith("server.example.test꞉3389.rdp", path);
        Assert.False(char.IsHighSurrogate(Path.GetFileNameWithoutExtension(path)[^1]));
    }
}
