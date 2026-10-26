using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Data;
using System.Windows.Markup;
using ConectaTI.App.Models;

namespace ConectaTI.App.Localization;

public interface ILanguageObserver { void OnLanguageChanged(); }

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Current { get; } = new();
    private readonly ResourceManager _resources = new("ConectaTI.App.Localization.Strings", typeof(LocalizationService).Assembly);
    private readonly List<WeakReference<ILanguageObserver>> _observers = new();
    public CultureInfo WindowsLanguage { get; } = CultureInfo.CurrentUICulture;
    private LocalizationService() { EffectiveCulture = Resolve(AppLanguage.Automatic, WindowsLanguage); }
    public AppLanguage SelectedLanguage { get; private set; }
    public CultureInfo EffectiveCulture { get; private set; } = CultureInfo.GetCultureInfo("pt-BR");
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => _resources.GetString(key, EffectiveCulture) ?? key;
    public static CultureInfo Resolve(AppLanguage choice, CultureInfo windows) => CultureInfo.GetCultureInfo(choice switch
    {
        AppLanguage.Portuguese => "pt-BR", AppLanguage.English => "en", AppLanguage.Spanish => "es",
        _ => windows.TwoLetterISOLanguageName switch { "en" => "en", "es" => "es", _ => "pt-BR" }
    });
    public void Observe(ILanguageObserver observer)
    {
        _observers.RemoveAll(w => !w.TryGetTarget(out _));
        _observers.Add(new(observer));
    }
    public void SetLanguage(AppLanguage language)
    {
        SelectedLanguage = Enum.IsDefined(language) ? language : AppLanguage.Automatic;
        EffectiveCulture = Resolve(SelectedLanguage, WindowsLanguage);
        // Formatting of persisted numbers, dates and import formats does not depend on UI language.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        foreach (var reference in _observers.ToArray())
            if (reference.TryGetTarget(out var observer)) observer.OnLanguageChanged();
        _observers.RemoveAll(w => !w.TryGetTarget(out _));
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{Key}]")
    { Source = LocalizationService.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public sealed record LocalizedMessage(string Source, object?[] Arguments)
{
    public override string ToString() => L.Render(this, Source);
}

public static class L
{
    private sealed record Rendered(string Source, object?[] Args);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Rendered> Recent = new(StringComparer.Ordinal);
    public static string Key(string source) => "Text" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    public static string Text(string source) => Format(source);
    public static string Format(string source, params object?[] args)
    {
        var key = Key(source);
        var template = LocalizationService.Current[key];
        if (template == key) template = source;
        var rendered = args.Length == 0 ? template : string.Format(LocalizationService.Current.EffectiveCulture, template, args);
        lock (Gate)
        {
            if (Recent.Count >= 1024) Recent.Clear();
            Recent[rendered] = new(source, args);
        }
        return rendered;
    }
    public static string Refresh(string value)
    {
        Rendered? found;
        lock (Gate) Recent.TryGetValue(value, out found);
        return found == null ? value : Format(found.Source, found.Args);
    }
    public static LocalizedMessage? Capture(string value)
    {
        lock (Gate) return Recent.TryGetValue(value, out var found) ? new(found.Source, found.Args) : null;
    }
    public static string Render(LocalizedMessage? message, string fallback) => message == null ? fallback : Format(message.Source, message.Arguments);
    public static object AsMessage(string? value) => value == null ? string.Empty : (object?)Capture(value) ?? value;
    private sealed record MessageList(object[] Messages)
    { public override string ToString() => string.Join(" ", Messages.Select(message => message.ToString())); }
    public static object JoinMessages(IEnumerable<string> messages) => new MessageList(messages.Select(AsMessage).ToArray());
}

public sealed class FilterLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text ? L.Text(text) : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
