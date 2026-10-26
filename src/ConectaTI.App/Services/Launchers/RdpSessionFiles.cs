using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ConectaTI.App.Services.Launchers;

/// <summary>Identificação legível do MSTSC, com isolamento por abertura, não por cadastro.</summary>
internal static class RdpSessionFiles
{
    private const string LeaseFileName = ".active";
    private const int MaxNameLength = 100;

    internal static string CreatePath(string root, string? accessName, string? clientName)
    {
        var name = string.IsNullOrWhiteSpace(accessName) ? clientName : accessName;
        var safeName = new StringBuilder();
        foreach (var ch in name ?? string.Empty)
            safeName.Append(char.IsControl(ch) || Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);

        var stem = safeName.ToString().Trim().TrimEnd('.', ' ');
        // Limita por elementos de texto para não cortar pares substitutos/acentos combinantes.
        var elements = StringInfo.ParseCombiningCharacters(stem);
        if (stem.Length > MaxNameLength)
        {
            var cut = elements.LastOrDefault(index => index <= MaxNameLength);
            stem = stem[..cut].TrimEnd('.', ' ');
        }
        if (string.IsNullOrWhiteSpace(stem))
            stem = "Conexao";

        // Nomes DOS continuam reservados mesmo com extensão (.rdp ou outra).
        var device = stem.Split('.')[0].TrimEnd(' ');
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (device.Length == 4 &&
             (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(device[3])))
            stem = "_" + stem;

        // O MSTSC acrescenta o full address ao título: não duplicá-lo no nome do arquivo.
        return Path.Combine(root, Guid.NewGuid().ToString("N"), stem + ".rdp");
    }

    internal static FileStream AcquireLease(string rdpPath)
    {
        var directory = Path.GetDirectoryName(rdpPath)!;
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, LeaseFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal static async Task CleanupOnExitAsync(Process process, FileStream lease, string rdpPath)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // A limpeza por idade poderá recolher a pasta se a observação falhar.
            return;
        }
        finally
        {
            process.Dispose();
            lease.Dispose();
        }

        TryCleanupDirectory(Path.GetDirectoryName(rdpPath)!, onlyIfOld: false);
    }

    internal static void CleanupAbandoned(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                TryCleanupDirectory(directory, onlyIfOld: true);
        }
    }

    private static void TryCleanupDirectory(string directory, bool onlyIfOld)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*.rdp");
            if (onlyIfOld && (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) <= TimeSpan.FromMinutes(15) ||
                files.Any(file => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= TimeSpan.FromMinutes(15))))
                return;

            // Uma sessão ativa conserva o lease aberto, inclusive ao minimizar/reconectar.
            var leasePath = Path.Combine(directory, LeaseFileName);
            using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var file in files)
                    File.Delete(file);
            }
            File.Delete(leasePath);
            Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // Em uso ou conteúdo inesperado: preservar e tentar na próxima limpeza normal.
        }
    }
}
