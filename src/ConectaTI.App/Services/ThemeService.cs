using System.Windows;
using System.Windows.Media;
using ConectaTI.App.Models;
using Microsoft.Win32;

namespace ConectaTI.App.Services;

public static class ThemeService
{
    public static bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal)
            {
                return intVal == 0;
            }
        }
        catch
        {
        }
        return false;
    }

    public static bool ResolveIsDark(AppThemeMode mode)
    {
        return mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => IsWindowsInDarkMode()
        };
    }

    public static void ApplyTheme(AppThemeMode mode, ResourceDictionary? targetResources = null)
    {
        var resources = targetResources ?? Application.Current?.Resources;
        if (resources == null)
            return;

        var isDark = ResolveIsDark(mode);

        if (isDark)
        {
            SetBrush(resources, "AppBackgroundBrush", "#0F172A");
            SetBrush(resources, "SurfaceBrush", "#1E293B");
            SetBrush(resources, "SurfaceAltBrush", "#273549");
            SetBrush(resources, "HeaderBackgroundBrush", "#0B1120");
            SetBrush(resources, "TableHeaderBackgroundBrush", "#162032");
            SetBrush(resources, "TableHeaderForegroundBrush", "#CBD5E1");
            SetBrush(resources, "BorderBrush", "#334155");
            SetBrush(resources, "PrimaryTextBrush", "#F8FAFC");
            SetBrush(resources, "SecondaryTextBrush", "#CBD5E1");
            SetBrush(resources, "MutedTextBrush", "#94A3B8");
            SetBrush(resources, "AccentBrush", "#0284C7");
            SetBrush(resources, "AccentHoverBrush", "#0EA5E9");
            SetBrush(resources, "AccentTextBrush", "#FFFFFF");
            SetBrush(resources, "SuccessBrush", "#4ADE80");
            SetBrush(resources, "WarningBrush", "#FBBF24");
            SetBrush(resources, "DangerBrush", "#F87171");
            SetBrush(resources, "InputBackgroundBrush", "#0F172A");
            SetBrush(resources, "SelectionBackgroundBrush", "#0369A1");
            SetBrush(resources, "SelectionTextBrush", "#FFFFFF");
            SetBrush(resources, "LockOverlayBrush", "#E6090D16");
        }
        else
        {
            SetBrush(resources, "AppBackgroundBrush", "#F1F5F9");
            SetBrush(resources, "SurfaceBrush", "#FFFFFF");
            SetBrush(resources, "SurfaceAltBrush", "#F8FAFC");
            SetBrush(resources, "HeaderBackgroundBrush", "#0F172A");
            SetBrush(resources, "TableHeaderBackgroundBrush", "#F1F5F9");
            SetBrush(resources, "TableHeaderForegroundBrush", "#334155");
            SetBrush(resources, "BorderBrush", "#CBD5E1");
            SetBrush(resources, "PrimaryTextBrush", "#0F172A");
            SetBrush(resources, "SecondaryTextBrush", "#475569");
            SetBrush(resources, "MutedTextBrush", "#64748B");
            SetBrush(resources, "AccentBrush", "#0284C7");
            SetBrush(resources, "AccentHoverBrush", "#0369A1");
            SetBrush(resources, "AccentTextBrush", "#FFFFFF");
            SetBrush(resources, "SuccessBrush", "#15803D");
            SetBrush(resources, "WarningBrush", "#B45309");
            SetBrush(resources, "DangerBrush", "#DC2626");
            SetBrush(resources, "InputBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "SelectionBackgroundBrush", "#E0F2FE");
            SetBrush(resources, "SelectionTextBrush", "#0369A1");
            SetBrush(resources, "LockOverlayBrush", "#E60F172A");
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
