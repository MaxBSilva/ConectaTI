using System.Globalization;
using System.Windows;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

public static class ViewPreferencesService
{
    public static void Normalize(AppSettings settings)
    {
        settings.FontSize = double.IsFinite(settings.FontSize) ? Math.Clamp(settings.FontSize, 11, 18) : 12.5;
        settings.LeftPaneWidth = double.IsFinite(settings.LeftPaneWidth) ? Math.Clamp(settings.LeftPaneWidth, 130, 650) : 220;
        settings.RightPaneWidth = double.IsFinite(settings.RightPaneWidth) ? Math.Clamp(settings.RightPaneWidth, 260, 1000) : 395;
        if (!Enum.IsDefined(settings.Language)) settings.Language = AppLanguage.Automatic;
        settings.ImportColumnWidths ??= new();
        foreach (var key in settings.ImportColumnWidths.Keys.ToArray())
            if (!double.IsFinite(settings.ImportColumnWidths[key]) || settings.ImportColumnWidths[key] < 40 || settings.ImportColumnWidths[key] > 700)
                settings.ImportColumnWidths.Remove(key);
    }

    public static void Apply(AppSettings settings)
    {
        Normalize(settings);
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        foreach (var size in new[] { 9d, 10, 10.5, 11, 11.5, 12, 12.5, 13, 13.5, 14, 15, 16, 17, 18, 20 })
            resources["Font" + size.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', '_')] = settings.FontSize * size / 12.5;
        resources["AppFontSize"] = settings.FontSize;
        resources["ConnectionItemPadding"] = settings.ComfortableMode ? new Thickness(6, 7, 6, 7) : new Thickness(3, 1, 3, 1);
    }
}
