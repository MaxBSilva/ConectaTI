using System.Globalization;
using System.Windows;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

public static class ViewPreferencesService
{
    public static readonly double[] FontSizePresets = [11, 12.5, 18];
    public static int FontSizePresetIndex(double size) => Enumerable.Range(0, FontSizePresets.Length)
        .MinBy(index => Math.Abs(FontSizePresets[index] - size));

    public static void Normalize(AppSettings settings)
    {
        settings.FontSize = double.IsFinite(settings.FontSize) ? Math.Clamp(settings.FontSize, 11, 18) : 12.5;
        settings.LeftPaneWidth = double.IsFinite(settings.LeftPaneWidth) ? Math.Clamp(settings.LeftPaneWidth, 130, 650) : 220;
        settings.RightPaneWidth = double.IsFinite(settings.RightPaneWidth) ? Math.Clamp(settings.RightPaneWidth, 260, 1000) : 395;
        if (!Enum.IsDefined(settings.Language)) settings.Language = AppLanguage.Automatic;
        if (!Enum.IsDefined(settings.Density)) settings.Density = InterfaceDensity.Standard;
        if (!double.IsFinite(settings.LeftPaneRatio) || settings.LeftPaneRatio <= 0 || settings.LeftPaneRatio >= 1)
            settings.LeftPaneRatio = double.NaN;
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
        var compact = settings.Density == InterfaceDensity.Compact;
        var comfortable = settings.Density == InterfaceDensity.Comfortable;
        Thickness Pick(Thickness small, Thickness normal, Thickness large) => compact ? small : comfortable ? large : normal;
        resources["ConnectionItemPadding"] = Pick(new(3, 1, 3, 1), new(5, 4, 5, 4), new(6, 7, 6, 7));
        resources["ConnectionItemMinHeight"] = compact ? 22d : comfortable ? 38d : 30d;
        resources["FieldPadding"] = Pick(new(6, 3, 6, 3), new(7, 5, 7, 5), new(9, 8, 9, 8));
        resources["ButtonPadding"] = Pick(new(8, 3, 8, 3), new(10, 5, 10, 5), new(12, 8, 12, 8));
        resources["ActionButtonPadding"] = Pick(new(10, 4, 10, 4), new(14, 6, 14, 6), new(16, 9, 16, 9));
        resources["HeaderButtonPadding"] = Pick(new(7, 3, 7, 3), new(9, 4, 9, 4), new(11, 6, 11, 6));
        resources["CopyButtonPadding"] = Pick(new(4, 2, 4, 2), new(6, 3, 6, 3), new(8, 5, 8, 5));
        resources["DetailPanePadding"] = Pick(new(8, 6, 8, 6), new(12, 10, 12, 10), new(16, 14, 16, 14));
        resources["SectionPadding"] = Pick(new(6, 4, 6, 4), new(10, 8, 10, 8), new(12, 10, 12, 10));
        resources["FieldGroupMargin"] = new Thickness(0, 0, 0, compact ? 6 : comfortable ? 14 : 10);
        resources["FieldLabelMargin"] = new Thickness(0, 0, 0, compact ? 2 : comfortable ? 5 : 3);
        resources["FilterButtonPadding"] = new Thickness(0, compact ? 2 : comfortable ? 5 : 3, 0, compact ? 2 : comfortable ? 5 : 3);
        resources["HeaderPadding"] = Pick(new(8, 4, 8, 4), new(10, 6, 10, 6), new(12, 8, 12, 8));
    }
}
