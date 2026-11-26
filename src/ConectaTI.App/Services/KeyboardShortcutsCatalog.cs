using System.Text;
using System.Windows;
using System.Windows.Input;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services;

public sealed record KeyboardShortcutItem(
    string Shortcut,
    string Action,
    string Context,
    string Description,
    string ActionId = "",
    string DefaultShortcut = "",
    bool IsConfigurable = false)
{
    public bool IsCustomized => IsConfigurable &&
        !string.IsNullOrWhiteSpace(DefaultShortcut) &&
        !string.Equals(Shortcut, DefaultShortcut, StringComparison.OrdinalIgnoreCase);

    public Visibility ConfigurableVisibility => IsConfigurable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => IsConfigurable ? Visibility.Collapsed : Visibility.Visible;
}

public static class KeyboardShortcutsCatalog
{
    public const string ActionNewConnection = "NewConnection";
    public const string ActionSaveConnection = "SaveConnection";
    public const string ActionSearchConnection = "SearchConnection";
    public const string ActionFocusConnectionName = "FocusConnectionName";
    public const string ActionOpenSettings = "OpenSettings";
    public const string ActionOpenShortcutsHelp = "OpenShortcutsHelp";

    private static readonly HashSet<string> ReservedTextEditingShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+C",
        "Ctrl+V",
        "Ctrl+X",
        "Ctrl+A",
        "Ctrl+Z",
        "Ctrl+Y"
    };

    private sealed record ConfigurableActionSpec(
        string ActionId,
        string DefaultShortcut,
        string ActionSource,
        string ContextSource,
        string DescriptionSource);

    private static readonly ConfigurableActionSpec[] ConfigurableSpecs =
    [
        new(ActionNewConnection, "Ctrl+N", "Nova conexão", "Janela principal", "Cria um novo rascunho em memória e foca o campo Nome."),
        new(ActionSaveConnection, "Ctrl+S", "Salvar alterações", "Janela principal", "Persiste explicitamente os dados e a intenção de senha do acesso atual."),
        new(ActionSearchConnection, "Ctrl+F", "Buscar conexão", "Janela principal", "Foca o campo Buscar e seleciona o filtro atual."),
        new(ActionFocusConnectionName, "F2", "Focar nome da conexão", "Lista ou formulário", "Move o foco diretamente para o campo Nome do acesso em edição."),
        new(ActionOpenSettings, "Ctrl+,", "Abrir Configurações", "Janela principal", "Abre a janela de Configurações do aplicativo."),
        new(ActionOpenShortcutsHelp, "F1", "Consultar atalhos", "Janela principal", "Abre diretamente a aba Atalhos nas Configurações.")
    ];

    public static IReadOnlyList<KeyboardShortcutItem> GetAll(AppSettings? settings = null)
    {
        var list = new List<KeyboardShortcutItem>(11);

        foreach (var spec in ConfigurableSpecs)
        {
            var effective = GetEffectiveShortcut(settings, spec.ActionId);
            list.Add(new KeyboardShortcutItem(
                Shortcut: effective,
                Action: L.Text(spec.ActionSource),
                Context: L.Text(spec.ContextSource),
                Description: L.Text(spec.DescriptionSource),
                ActionId: spec.ActionId,
                DefaultShortcut: spec.DefaultShortcut,
                IsConfigurable: true));
        }

        list.Add(new("Enter", L.Text("Conectar"), L.Text("Lista de acessos"), L.Text("Inicia a conexão selecionada (não dispara durante a edição de campos).")));
        list.Add(new("↑ / ↓", L.Text("Navegar pelos acessos"), L.Text("Lista de acessos"), L.Text("Move um item visível por vez mantendo seleção, foco e rolagem sincronizados.")));
        list.Add(new("A–Z / 0–9", L.Text("Busca incremental"), L.Text("Lista de acessos"), L.Text("Localiza rapidamente pelo nome ao digitar com a lista em foco.")));
        list.Add(new("Esc", L.Text("Limpar busca / Fechar"), L.Text("Busca, lista ou diálogos"), L.Text("Limpa o filtro quando a caixa de busca está em foco, encerra a busca incremental ou fecha diálogos sem apagar rascunhos.")));
        list.Add(new("Tab / Shift+Tab", L.Text("Navegar entre controles"), L.Text("Todas as janelas"), L.Text("Percorre comandos, filtros, lista e campos em ordem lógica com foco visível.")));

        return list;
    }

    public static string GetDefaultShortcut(string actionId)
    {
        var spec = Array.Find(ConfigurableSpecs, s => string.Equals(s.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        return spec?.DefaultShortcut ?? string.Empty;
    }

    public static string GetEffectiveShortcut(AppSettings? settings, string actionId)
    {
        var defaultShortcut = GetDefaultShortcut(actionId);
        if (string.IsNullOrEmpty(defaultShortcut) || settings?.CustomShortcuts is null)
            return defaultShortcut;

        if (settings.CustomShortcuts.TryGetValue(actionId, out var custom) &&
            TryNormalizeShortcutString(custom, out var canonical))
        {
            return canonical;
        }

        return defaultShortcut;
    }

    public static Dictionary<string, string> SanitizeCustomShortcuts(Dictionary<string, string>? raw)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw is null || raw.Count == 0)
            return clean;

        var usedShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in ConfigurableSpecs)
        {
            if (raw.TryGetValue(spec.ActionId, out var candidate) &&
                TryNormalizeShortcutString(candidate, out var canonical) &&
                !string.Equals(canonical, spec.DefaultShortcut, StringComparison.OrdinalIgnoreCase) &&
                usedShortcuts.Add(canonical))
            {
                clean[spec.ActionId] = canonical;
            }
        }

        // Remove customizações que colidam com o atalho padrão de outra ação não customizada
        foreach (var spec in ConfigurableSpecs)
        {
            if (!clean.ContainsKey(spec.ActionId))
            {
                var collidingKey = clean.FirstOrDefault(kv => string.Equals(kv.Value, spec.DefaultShortcut, StringComparison.OrdinalIgnoreCase)).Key;
                if (!string.IsNullOrEmpty(collidingKey))
                {
                    clean.Remove(collidingKey);
                }
            }
        }

        return clean;
    }

    public static bool TryFormatShortcutFromKeyEvent(KeyEventArgs e, out string formatted, out bool isReservedEditingCombo)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return TryFormatShortcutFromWpfKey(key, Keyboard.Modifiers, out formatted, out isReservedEditingCombo);
    }

    public static bool TryFormatShortcutFromWpfKey(
        Key key,
        ModifierKeys modifiers,
        out string formatted,
        out bool isReservedEditingCombo)
    {
        formatted = string.Empty;
        isReservedEditingCombo = false;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.DeadCharProcessed)
        {
            return false;
        }

        var isFunctionKey = key >= Key.F1 && key <= Key.F12;
        var baseName = GetBaseKeyName(key);
        if (string.IsNullOrEmpty(baseName))
            return false;

        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        if (!isFunctionKey && !ctrl && !alt)
            return false;

        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        sb.Append(baseName);

        var candidate = sb.ToString();
        if (ReservedTextEditingShortcuts.Contains(candidate))
        {
            isReservedEditingCombo = true;
            return false;
        }

        formatted = candidate;
        return true;
    }

    public static bool TryNormalizeShortcutString(string? raw, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        bool ctrl = false, alt = false, shift = false;
        string? baseToken = null;

        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else if (baseToken is null && i == parts.Length - 1)
            {
                baseToken = token;
            }
            else
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(baseToken))
            return false;

        if (!TryNormalizeBaseToken(baseToken, out var normalizedBase, out var isFunctionKey))
            return false;

        if (!isFunctionKey && !ctrl && !alt)
            return false;

        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        sb.Append(normalizedBase);

        var built = sb.ToString();
        if (ReservedTextEditingShortcuts.Contains(built))
            return false;

        canonical = built;
        return true;
    }

    public static bool TryAssignShortcut(
        AppSettings settings,
        string actionId,
        string newShortcut,
        out string? conflictActionDisplayName)
    {
        conflictActionDisplayName = null;
        var spec = Array.Find(ConfigurableSpecs, s => string.Equals(s.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (spec is null)
            return false;

        if (!TryNormalizeShortcutString(newShortcut, out var canonical))
            return false;

        settings.CustomShortcuts ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var other in ConfigurableSpecs)
        {
            if (string.Equals(other.ActionId, spec.ActionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var otherEffective = GetEffectiveShortcut(settings, other.ActionId);
            if (string.Equals(otherEffective, canonical, StringComparison.OrdinalIgnoreCase))
            {
                conflictActionDisplayName = L.Text(other.ActionSource);
                return false;
            }
        }

        if (string.Equals(canonical, spec.DefaultShortcut, StringComparison.OrdinalIgnoreCase))
        {
            settings.CustomShortcuts.Remove(spec.ActionId);
        }
        else
        {
            settings.CustomShortcuts[spec.ActionId] = canonical;
        }

        return true;
    }

    public static void ResetActionShortcut(AppSettings settings, string actionId)
    {
        if (settings.CustomShortcuts is null)
            return;

        settings.CustomShortcuts.Remove(actionId);
    }

    public static void ResetAllShortcuts(AppSettings settings)
    {
        settings.CustomShortcuts?.Clear();
    }

    public static bool TryMatchAction(AppSettings? settings, Key key, ModifierKeys modifiers, out string actionId)
    {
        actionId = string.Empty;
        if (!TryFormatShortcutFromWpfKey(key, modifiers, out var pressed, out _))
            return false;

        foreach (var spec in ConfigurableSpecs)
        {
            var effective = GetEffectiveShortcut(settings, spec.ActionId);
            if (string.Equals(effective, pressed, StringComparison.OrdinalIgnoreCase))
            {
                actionId = spec.ActionId;
                return true;
            }
        }

        return false;
    }

    private static string GetBaseKeyName(Key key)
    {
        if (key >= Key.F1 && key <= Key.F12)
            return "F" + ((int)key - (int)Key.F1 + 1);
        if (key >= Key.A && key <= Key.Z)
            return ((char)('A' + ((int)key - (int)Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9)
            return ((char)('0' + ((int)key - (int)Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return ((char)('0' + ((int)key - (int)Key.NumPad0))).ToString();

        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod or Key.Decimal => ".",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemPlus or Key.Add => "=",
            Key.OemQuestion or Key.Divide => "/",
            Key.OemSemicolon => ";",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.Space => "Espaço",
            _ => string.Empty
        };
    }

    private static bool TryNormalizeBaseToken(string token, out string normalized, out bool isFunctionKey)
    {
        normalized = string.Empty;
        isFunctionKey = false;

        if (token.Length is 2 or 3 &&
            (token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token.AsSpan(1), out var fn) &&
            fn >= 1 && fn <= 12)
        {
            normalized = $"F{fn}";
            isFunctionKey = true;
            return true;
        }

        if (token.Length == 1)
        {
            var c = token[0];
            if (c is >= 'a' and <= 'z')
            {
                normalized = char.ToUpperInvariant(c).ToString();
                return true;
            }
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c is ',' or '.' or '-' or '=' or '/' or ';' or '[' or ']')
            {
                normalized = c.ToString();
                return true;
            }
        }

        if (string.Equals(token, "Espaço", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Espaco", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Espaço";
            return true;
        }

        return false;
    }
}
