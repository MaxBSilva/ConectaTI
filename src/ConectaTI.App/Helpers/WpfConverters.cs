using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ConectaTI.App.Models;

namespace ConectaTI.App.Helpers;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EnvironmentToBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ProdBrush = FreezeBrush("#DC2626");
    private static readonly SolidColorBrush StagingBrush = FreezeBrush("#D97706");
    private static readonly SolidColorBrush TestBrush = FreezeBrush("#0284C7");
    private static readonly SolidColorBrush OtherBrush = FreezeBrush("#475569");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is EnvironmentType env)
        {
            return env switch
            {
                EnvironmentType.Production => ProdBrush,
                EnvironmentType.Staging => StagingBrush,
                EnvironmentType.Testing => TestBrush,
                _ => OtherBrush
            };
        }
        return OtherBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public class FavoriteStarTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "★" : "☆";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
