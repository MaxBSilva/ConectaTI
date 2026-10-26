using System.Text.Json;
using System.Text.Json.Serialization;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

public class SettingsService
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public AppSettings Current { get; private set; } = new();

    public SettingsService(AppPaths paths, AppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsPath))
            {
                var json = File.ReadAllText(_paths.SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    using var document = JsonDocument.Parse(json);
                    var properties = document.RootElement.EnumerateObject().ToArray();
                    if (!properties.Any(p => p.Name.Equals(nameof(AppSettings.Density), StringComparison.OrdinalIgnoreCase)))
                    {
                        var legacy = properties.FirstOrDefault(p => p.Name.Equals(nameof(AppSettings.ComfortableMode), StringComparison.OrdinalIgnoreCase));
                        if (legacy.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            loaded.ComfortableMode = legacy.Value.GetBoolean();
                    }
                    if (loaded.ClipboardClearSeconds == 20 || loaded.ClipboardClearSeconds <= 0)
                    {
                        loaded.ClipboardClearSeconds = 30;
                    }
                    ViewPreferencesService.Normalize(loaded);
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Settings", $"Falha ao carregar settings.json, utilizando padrões: {ex.Message}");
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save()
    {
        try
        {
            _paths.EnsureDirectories();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tempFile = _paths.SettingsPath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _paths.SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Settings", "Erro ao salvar configurações do aplicativo.", ex);
        }
    }

    /// <summary>
    /// Garante que a posição e o tamanho salvos da janela estejam dentro da área virtual visível dos monitores.
    /// Evita janelas perdidas fora da tela quando um monitor externo é desconectado.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) ClampWindowBounds(
        double savedLeft,
        double savedTop,
        double savedWidth,
        double savedHeight,
        double virtualLeft,
        double virtualTop,
        double virtualWidth,
        double virtualHeight)
    {
        var width = double.IsNaN(savedWidth) || double.IsInfinity(savedWidth) || savedWidth < 460 ? 620 : savedWidth;
        var height = double.IsNaN(savedHeight) || double.IsInfinity(savedHeight) || savedHeight < 360 ? 500 : savedHeight;

        if (virtualWidth > 0 && width > virtualWidth)
            width = Math.Max(460, virtualWidth - 40);

        if (virtualHeight > 0 && height > virtualHeight)
            height = Math.Max(360, virtualHeight - 40);

        if (double.IsNaN(savedLeft) || double.IsInfinity(savedLeft) ||
            double.IsNaN(savedTop) || double.IsInfinity(savedTop) ||
            virtualWidth <= 0 || virtualHeight <= 0)
        {
            var centerLeft = virtualLeft + Math.Max(0, (virtualWidth - width) / 2);
            var centerTop = virtualTop + Math.Max(0, (virtualHeight - height) / 2);
            return (centerLeft, centerTop, width, height);
        }

        var minVisibleMargin = 100.0;
        var left = savedLeft;
        var top = savedTop;

        if (left + minVisibleMargin > virtualLeft + virtualWidth || left + width - minVisibleMargin < virtualLeft)
        {
            left = virtualLeft + Math.Max(0, (virtualWidth - width) / 2);
        }

        if (top + minVisibleMargin > virtualTop + virtualHeight || top < virtualTop)
        {
            top = virtualTop + Math.Max(0, (virtualHeight - height) / 2);
        }

        return (left, top, width, height);
    }
}
