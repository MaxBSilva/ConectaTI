using ConectaTI.App.Models;

namespace ConectaTI.App.Services.Launchers;

public static class ExternalClientDetector
{
    public const string DefaultMstscEnvironmentPath = @"%WINDIR%\System32\mstsc.exe";

    /// <summary>
    /// Preenche apenas os caminhos que ainda estão em branco nas configurações.
    /// Nunca sobrescreve um caminho já configurado pelo usuário.
    /// </summary>
    public static void AutoPopulateMissingPaths(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MstscExecutablePath))
            settings.MstscExecutablePath = GetDefaultMstscPath();

        if (string.IsNullOrWhiteSpace(settings.PuttyExecutablePath))
            settings.PuttyExecutablePath = DetectPutty() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(settings.OpenSshExecutablePath))
            settings.OpenSshExecutablePath = DetectOpenSsh() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(settings.WinScpExecutablePath))
            settings.WinScpExecutablePath = DetectWinScp() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(settings.VncExecutablePath))
            settings.VncExecutablePath = DetectVncViewer(out _) ?? string.Empty;
    }

    public static string ResolveConfiguredPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        var trimmed = rawPath.Trim().Trim('"');
        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    public static bool IsExecutableValid(string? rawPath, out string resolvedPath)
    {
        resolvedPath = ResolveConfiguredPath(rawPath);
        return !string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath);
    }

    public static string GetDefaultMstscPath()
    {
        var expandedWinDir = ResolveConfiguredPath(DefaultMstscEnvironmentPath);
        if (!string.IsNullOrWhiteSpace(expandedWinDir) && File.Exists(expandedWinDir))
            return expandedWinDir;

        return DetectMstsc() ?? expandedWinDir;
    }

    public static string? DetectMstsc()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return FindFirstExisting(
            !string.IsNullOrWhiteSpace(winDir) ? Path.Combine(winDir, "System32", "mstsc.exe") : null,
            Path.Combine(Environment.SystemDirectory, "mstsc.exe"),
            @"C:\Windows\System32\mstsc.exe",
            FindInPath("mstsc.exe"));
    }

    public static string? DetectPutty()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirstExisting(
            @"C:\Program Files\PuTTY\putty.exe",
            @"C:\Program Files (x86)\PuTTY\putty.exe",
            Path.Combine(localAppData, @"Programs\PuTTY\putty.exe"),
            FindInPath("putty.exe"));
    }

    public static string? DetectOpenSsh()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return FindFirstExisting(
            !string.IsNullOrWhiteSpace(winDir) ? Path.Combine(winDir, @"System32\OpenSSH\ssh.exe") : null,
            Path.Combine(Environment.SystemDirectory, @"OpenSSH\ssh.exe"),
            @"C:\Windows\System32\OpenSSH\ssh.exe",
            @"C:\Program Files\OpenSSH\ssh.exe",
            FindInPath("ssh.exe"));
    }

    public static string? DetectWinScp()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirstExisting(
            @"C:\Program Files (x86)\WinSCP\WinSCP.exe",
            @"C:\Program Files\WinSCP\WinSCP.exe",
            Path.Combine(localAppData, @"Programs\WinSCP\WinSCP.exe"),
            FindInPath("WinSCP.exe"));
    }

    public static string? DetectVncViewer(out VncViewerFlavor flavor)
    {
        flavor = VncViewerFlavor.TigerRealVnc;

        var tigerOrReal = FindFirstExisting(
            @"C:\Program Files\TigerVNC\vncviewer.exe",
            @"C:\Program Files (x86)\TigerVNC\vncviewer.exe",
            @"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe",
            @"C:\Program Files (x86)\RealVNC\VNC Viewer\vncviewer.exe");
        if (tigerOrReal != null)
        {
            flavor = VncViewerFlavor.TigerRealVnc;
            return tigerOrReal;
        }

        var tightVnc = FindFirstExisting(
            @"C:\Program Files\TightVNC\tvnviewer.exe",
            @"C:\Program Files (x86)\TightVNC\tvnviewer.exe");
        if (tightVnc != null)
        {
            flavor = VncViewerFlavor.TightVnc;
            return tightVnc;
        }

        var ultraVnc = FindFirstExisting(
            @"C:\Program Files\uvnc bvba\UltraVNC\vncviewer.exe",
            @"C:\Program Files (x86)\uvnc bvba\UltraVNC\vncviewer.exe");
        if (ultraVnc != null)
        {
            flavor = VncViewerFlavor.UltraVnc;
            return ultraVnc;
        }

        var inPath = FindInPath("vncviewer.exe") ?? FindInPath("tvnviewer.exe");
        if (inPath != null && inPath.EndsWith("tvnviewer.exe", StringComparison.OrdinalIgnoreCase))
            flavor = VncViewerFlavor.TightVnc;

        return inPath;
    }

    private static string? FindFirstExisting(params string?[] candidates)
    {
        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? FindInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignora entrada inválida no PATH
            }
        }
        return null;
    }
}
