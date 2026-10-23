namespace ConectaTI.App.Services;

public class AppPaths
{
    private static readonly string PointerFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConectaTI",
        "data_location.txt");

    public string RootDirectory { get; private set; }
    public string DatabasePath => Path.Combine(RootDirectory, "sqlite.db");
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string TempRdpDirectory => Path.Combine(RootDirectory, "temp");

    public bool IsDataDirectoryWritable { get; private set; }

    public AppPaths(string? customRootDirectory = null)
    {
        RootDirectory = ResolveRootDirectory(customRootDirectory);
        IsDataDirectoryWritable = CheckDirectoryWritable(RootDirectory);

        if (IsDataDirectoryWritable)
        {
            EnsureDirectories();
        }
    }

    public static string ResolveRootDirectory(string? customRootDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customRootDirectory))
        {
            return Path.GetFullPath(customRootDirectory);
        }

        // 1. Verifica se há ponteiro persistido de pasta customizada (quando executável estava em local somente leitura)
        var pointer = GetDataLocationPointer();
        if (!string.IsNullOrWhiteSpace(pointer) && Directory.Exists(pointer) && CheckDirectoryWritable(pointer))
        {
            return Path.GetFullPath(pointer);
        }

        // 2. Pasta padrão: AppContext.BaseDirectory (ao lado do executável)
        var appBase = AppContext.BaseDirectory;
        return Path.GetFullPath(appBase);
    }

    public static bool CheckDirectoryWritable(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var probeFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetDataLocationPointer()
    {
        try
        {
            if (File.Exists(PointerFilePath))
            {
                var content = File.ReadAllText(PointerFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }
        }
        catch
        {
        }
        return null;
    }

    public static void SaveDataLocationPointer(string targetDirectory)
    {
        try
        {
            var pointerDir = Path.GetDirectoryName(PointerFilePath)!;
            Directory.CreateDirectory(pointerDir);
            File.WriteAllText(PointerFilePath, targetDirectory.Trim());
        }
        catch
        {
        }
    }

    public void SwitchDataDirectory(string newDirectory)
    {
        RootDirectory = Path.GetFullPath(newDirectory);
        IsDataDirectoryWritable = CheckDirectoryWritable(RootDirectory);
        if (IsDataDirectoryWritable)
        {
            SaveDataLocationPointer(RootDirectory);
            EnsureDirectories();
        }
    }

    public void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(BackupsDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(TempRdpDirectory);
        }
        catch
        {
        }
    }

    public void CleanupTempRdpFiles()
    {
        try
        {
            if (!Directory.Exists(TempRdpDirectory))
                return;

            foreach (var file in Directory.GetFiles(TempRdpDirectory, "*.rdp"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(15))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignora arquivo temporário ainda em uso pelo mstsc
                }
            }
        }
        catch
        {
            // Ignora falha de limpeza temporária
        }
    }
}
