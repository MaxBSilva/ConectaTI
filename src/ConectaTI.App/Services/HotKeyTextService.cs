// Módulo interno de textos rápidos, baseado no HotKeyText v1.3.0.
// Código-fonte de referência por Vinícius da Veiga (https://github.com/viniciusdaveigaa/HotKeyText, commit 7b5791b39e59c96a4203ed3a71ad7e73f8f125ac).
// Adaptado para execução in-process em executável único (ConectaTI.exe) por Max Silva (https://github.com/MaxBSilva/ConectaTI).

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace ConectaTI.App.Services;

[XmlType("TextoItem")]
public sealed class TextoItem : INotifyPropertyChanged
{
    private string _titulo = string.Empty;
    private string _conteudo = string.Empty;
    private string _atalho = string.Empty;
    private bool _ocultarTexto;
    private int _displayIndex;

    [XmlElement("Titulo")]
    public string Titulo
    {
        get => _titulo;
        set
        {
            if (_titulo == (value ?? string.Empty)) return;
            _titulo = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    [XmlElement("Conteudo")]
    public string Conteudo
    {
        get => _conteudo;
        set
        {
            if (_conteudo == (value ?? string.Empty)) return;
            _conteudo = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayPreview));
        }
    }

    [XmlElement("Atalho")]
    public string Atalho
    {
        get => _atalho;
        set
        {
            if (_atalho == (value ?? string.Empty)) return;
            _atalho = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveShortcut));
        }
    }

    [XmlElement("OcultarTexto")]
    public bool OcultarTexto
    {
        get => _ocultarTexto;
        set
        {
            if (_ocultarTexto == value) return;
            _ocultarTexto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayPreview));
        }
    }

    [XmlIgnore]
    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex == value) return;
            _displayIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveShortcut));
        }
    }

    [XmlIgnore]
    public string EffectiveShortcut
    {
        get
        {
            var custom = (_atalho ?? string.Empty).Trim();
            return custom.Length > 0 ? custom : HotKeyTextService.AtalhoPadraoDoIndice(_displayIndex);
        }
    }

    [XmlIgnore]
    public string DisplayTitle => HotKeyTextService.TituloDe(this);

    [XmlIgnore]
    public string DisplayPreview => HotKeyTextService.ResumoDe(this);

    public void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(EffectiveShortcut));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayPreview));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

[XmlRoot("Config")]
public sealed class HotKeyTextConfig
{
    [XmlElement("ModoColar")]
    public bool ModoColar { get; set; }

    [XmlElement("TemaEscuro")]
    public bool TemaEscuro { get; set; } = true;

    [XmlArray("Textos")]
    [XmlArrayItem("TextoItem")]
    public List<TextoItem> Textos { get; set; } = [];
}

public sealed record StandaloneConflictCheckResult(
    bool HasConflict,
    int? StandaloneProcessId,
    string? StandaloneExecutablePath,
    string Message);

public static class HotKeyNative
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWNOACTIVATE = 4;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}

/// <summary>
/// Módulo interno do HotKeyText (v1.3.0 por Vinícius da Veiga) executado dentro do processo do ConectaTI.exe.
/// Mantém compatibilidade integral com %APPDATA%\HotKeyText\textos.xml, atalho global Scroll Lock,
/// detecção de sessão WTS/RDP, modos Digitar e Colar, ocultação de prévia (OcultarTexto) e detecção
/// de conflito caso uma edição avulsa do HotKeyText esteja em execução.
/// </summary>
public sealed class HotKeyTextService : IDisposable
{
    public const string ComponentName = "HotKeyText";
    public const string SourceRepository = "viniciusdaveigaa/HotKeyText";
    public const string SourceCommit = "7b5791b39e59c96a4203ed3a71ad7e73f8f125ac";
    public const string AuthorCredit = "Vinícius da Veiga";
    public const string OriginReference = "viniciusdaveigaa/HotKeyText@7b5791b39e59c96a4203ed3a71ad7e73f8f125ac";

    public const string DefaultMutexName = "HotKeyText_InstanciaUnica";
    public const string DefaultOpenManagerEventName = "HotKeyText_AbrirGerenciador";
    public const string DefaultShutdownEventName = "HotKeyText_EncerrarParaAtualizacao";
    public const string ConectaTiUpdateEventName = "ConectaTI_AbrirAtualizacoes";
    private const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultRunValueName = "ConectaTI_QuickTexts";

    private static readonly IntPtr SyntheticInputMark = new(0x484B5458); // 'HKTX'

    private readonly string _dataDirectory;
    private readonly string _xmlFilePath;
    private readonly string _instancePrefix;
    private readonly string _runValueName;
    private readonly Dispatcher _dispatcher;
    private readonly HotKeyNative.HookProc _kbProc;
    private readonly HotKeyNative.HookProc _msProc;
    private readonly HashSet<uint> _swallowedKeys = [];

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _msHook = IntPtr.Zero;
    private DispatcherTimer? _wtsPollingTimer;
    private QuickTextsPopupWindow? _popupWindow;
    private bool _scrollLockPressed;
    private int _isTyping;
    private bool _disposed;

    private IntPtr _mainWindowHandle = IntPtr.Zero;
    private IntPtr _lastExternalTargetWindow = IntPtr.Zero;
    private IntPtr _popupTargetWindow = IntPtr.Zero;

    public HotKeyTextConfig Config { get; private set; } = new();
    public bool IsHookActive { get; private set; }
    public bool HasStandaloneConflict { get; private set; }
    public string ConflictMessage { get; private set; } = string.Empty;
    public bool IsCapturingShortcut { get; set; }
    public bool IsPopupOpen { get; private set; }

    /// <summary>
    /// Permite emular janela WTS/RDP ou interceptar SendInput em testes automatizados sem afetar outras janelas do usuário.
    /// </summary>
    public bool ForceWtsModeForTest { get; set; }
    public Action<List<HotKeyNative.INPUT>>? SendInputInterceptorForTest { get; set; }
    public IntPtr ExplicitTargetWindowForTest { get; set; } = IntPtr.Zero;

    public event Action? ManageRequestedFromPopup;
    public event Action? StateChanged;

    public HotKeyTextService(
        string? customDataDirectory = null,
        string instancePrefix = "",
        string? customRunValueName = null,
        bool installGlobalHooks = true)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _instancePrefix = instancePrefix ?? string.Empty;
        _runValueName = string.IsNullOrWhiteSpace(customRunValueName) ? DefaultRunValueName : customRunValueName!;

        _dataDirectory = string.IsNullOrWhiteSpace(customDataDirectory)
            ? GetDefaultUserDataDirectory()
            : Path.GetFullPath(customDataDirectory);
        _xmlFilePath = Path.Combine(_dataDirectory, "textos.xml");

        _kbProc = KeyboardHookCallback;
        _msProc = MouseHookCallback;

        if (string.IsNullOrWhiteSpace(customDataDirectory))
        {
            MigrateLegacyTextosRapidosIfNeeded();
        }

        Load();

        if (installGlobalHooks)
        {
            TryActivateShortcuts();
        }
    }

    public string DataDirectory => _dataDirectory;
    public string XmlFilePath => _xmlFilePath;
    public IntPtr LastExternalTargetWindow => _lastExternalTargetWindow;

    public static string GetDefaultUserDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "HotKeyText");
    }

    public static string GetDefaultUserDataXmlPath()
        => Path.Combine(GetDefaultUserDataDirectory(), "textos.xml");

    public void AttachMainWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            _mainWindowHandle = helper.Handle;
        }
        else
        {
            window.SourceInitialized += (_, _) =>
            {
                _mainWindowHandle = new WindowInteropHelper(window).Handle;
            };
        }
    }

    public void RecordExternalWindowIfValid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !HotKeyNative.IsWindow(hwnd) || !HotKeyNative.IsWindowVisible(hwnd))
            return;

        var popupHwnd = _popupWindow?.Hwnd ?? IntPtr.Zero;
        if (hwnd == popupHwnd || hwnd == _mainWindowHandle)
            return;

        // Não sobrescreve com diálogos modais pertencentes ao próprio processo do ConectaTI
        HotKeyNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId && _mainWindowHandle != IntPtr.Zero)
            return;

        _lastExternalTargetWindow = hwnd;
    }

    public void CaptureCurrentForegroundAsTargetIfExternal()
    {
        var fg = HotKeyNative.GetForegroundWindow();
        RecordExternalWindowIfValid(fg);
    }

    /// <summary>
    /// Verifica se uma instância avulsa do HotKeyText está em execução (via Mutex, EventWaitHandle ou processo).
    /// </summary>
    public static StandaloneConflictCheckResult DetectStandaloneConflict(string instancePrefix = "")
    {
        var mutexName = (instancePrefix ?? string.Empty) + DefaultMutexName;
        var openEventName = (instancePrefix ?? string.Empty) + DefaultOpenManagerEventName;

        int? standalonePid = null;
        string? standalonePath = null;

        if (string.IsNullOrEmpty(instancePrefix))
        {
            foreach (var proc in Process.GetProcessesByName("HotKeyText"))
            {
                try
                {
                    if (proc.Id != Environment.ProcessId && !proc.HasExited)
                    {
                        standalonePid = proc.Id;
                        standalonePath = TryGetProcessExecutablePath(proc);
                        break;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        var namedObjectBusy = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(openEventName, out var existingEvent))
            {
                namedObjectBusy = true;
                existingEvent.Dispose();
            }
        }
        catch (UnauthorizedAccessException)
        {
            namedObjectBusy = true;
        }
        catch { }

        if (!namedObjectBusy)
        {
            try
            {
                if (Mutex.TryOpenExisting(mutexName, out var existingMutex))
                {
                    namedObjectBusy = true;
                    existingMutex.Dispose();
                }
            }
            catch (UnauthorizedAccessException)
            {
                namedObjectBusy = true;
            }
            catch { }
        }

        if (standalonePid.HasValue || namedObjectBusy)
        {
            var detail = standalonePid.HasValue
                ? $" (PID {standalonePid.Value})"
                : string.Empty;
            return new StandaloneConflictCheckResult(
                HasConflict: true,
                StandaloneProcessId: standalonePid,
                StandaloneExecutablePath: standalonePath,
                Message: $"A edição avulsa do HotKeyText está em execução{detail}. Feche o HotKeyText avulso antes de ativar os atalhos integrados do ConectaTI para evitar disputa pelo Scroll Lock.");
        }

        return new StandaloneConflictCheckResult(
            HasConflict: false,
            StandaloneProcessId: null,
            StandaloneExecutablePath: null,
            Message: string.Empty);
    }

    /// <summary>
    /// Ativa o hook global de teclado (Scroll Lock) e o polling WTS caso não haja conflito com edição avulsa.
    /// </summary>
    public bool TryActivateShortcuts(bool installNativeHook = true)
    {
        if (_disposed)
            return false;

        if (IsHookActive)
            return true;

        var conflict = DetectStandaloneConflict(_instancePrefix);
        if (conflict.HasConflict)
        {
            HasStandaloneConflict = true;
            ConflictMessage = conflict.Message;
            IsHookActive = false;
            StateChanged?.Invoke();
            return false;
        }

        var mutexName = _instancePrefix + DefaultMutexName;
        try
        {
            var mutex = new Mutex(true, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                HasStandaloneConflict = true;
                ConflictMessage = "A edição avulsa do HotKeyText já está segurando o controle de atalhos (Scroll Lock). Feche-a e clique em 'Ativar atalhos'.";
                IsHookActive = false;
                StateChanged?.Invoke();
                return false;
            }

            _instanceMutex = mutex;
            _ownsMutex = true;
        }
        catch (Exception ex)
        {
            HasStandaloneConflict = true;
            ConflictMessage = $"Não foi possível registrar o controle exclusivo de atalhos: {ex.Message}";
            IsHookActive = false;
            StateChanged?.Invoke();
            return false;
        }

        if (installNativeHook)
        {
            _kbHook = HotKeyNative.SetWindowsHookEx(
                HotKeyNative.WH_KEYBOARD_LL,
                _kbProc,
                HotKeyNative.GetModuleHandle(null),
                0);
        }

        _wtsPollingTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _wtsPollingTimer.Tick += OnWtsPollingTick;
        _wtsPollingTimer.Start();

        HasStandaloneConflict = false;
        ConflictMessage = string.Empty;
        IsHookActive = true;
        StateChanged?.Invoke();
        return true;
    }

    private void OnWtsPollingTick(object? sender, EventArgs e)
    {
        CaptureCurrentForegroundAsTargetIfExternal();

        if (IsCapturingShortcut)
            return;

        var down = (HotKeyNative.GetAsyncKeyState(0x91) & 0x8000) != 0;
        if (down && !_scrollLockPressed)
        {
            _scrollLockPressed = true;
            TogglePopup();
        }
        else if (!down && _scrollLockPressed)
        {
            _scrollLockPressed = false;
        }
    }

    public bool StartWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: false);
                return key?.GetValue(_runValueName) is not null;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunRegistrySubKey);
                if (key is null) return;
                if (value)
                {
                    var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ConectaTI.exe");
                    key.SetValue(_runValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(_runValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Ignorado em ambientes restritos
            }
        }
    }

    private void MigrateLegacyTextosRapidosIfNeeded()
    {
        try
        {
            if (File.Exists(_xmlFilePath))
                return;

            var legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TextosRapidos");
            var legacyXml = Path.Combine(legacyDir, "textos.xml");
            if (File.Exists(legacyXml))
            {
                Directory.CreateDirectory(_dataDirectory);
                File.Copy(legacyXml, _xmlFilePath, overwrite: true);
            }
        }
        catch { }
    }

    public void Load()
    {
        if (File.Exists(_xmlFilePath))
        {
            try
            {
                var serializer = new XmlSerializer(typeof(HotKeyTextConfig));
                using var fs = new FileStream(_xmlFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (serializer.Deserialize(fs) is HotKeyTextConfig loaded)
                {
                    loaded.Textos ??= [];
                    for (var i = 0; i < loaded.Textos.Count; i++)
                    {
                        loaded.Textos[i].Atalho ??= string.Empty;
                        loaded.Textos[i].Titulo ??= string.Empty;
                        loaded.Textos[i].Conteudo ??= string.Empty;
                        loaded.Textos[i].DisplayIndex = i;
                    }
                    Config = loaded;
                    return;
                }
            }
            catch
            {
                try { File.Copy(_xmlFilePath, _xmlFilePath + ".erro", overwrite: true); } catch { }
            }
        }

        Config = new HotKeyTextConfig
        {
            ModoColar = false,
            TemaEscuro = true,
            Textos =
            [
                new TextoItem
                {
                    Titulo = "Assinatura",
                    Conteudo = "Atenciosamente,\r\nVinícius.",
                    Atalho = "1",
                    OcultarTexto = false,
                    DisplayIndex = 0
                }
            ]
        };
        Save();
    }

    public void Save()
    {
        try
        {
            ReindexItems();
            Directory.CreateDirectory(_dataDirectory);
            var tmp = _xmlFilePath + ".tmp";
            var serializer = new XmlSerializer(typeof(HotKeyTextConfig));
            using (var fs = File.Create(tmp))
            {
                serializer.Serialize(fs, Config);
            }

            if (File.Exists(_xmlFilePath))
                File.Replace(tmp, _xmlFilePath, null);
            else
                File.Move(tmp, _xmlFilePath);
        }
        catch
        {
            // Ignorado se diretório somente leitura
        }
    }

    public void ReindexItems()
    {
        for (var i = 0; i < Config.Textos.Count; i++)
        {
            Config.Textos[i].DisplayIndex = i;
            Config.Textos[i].RefreshComputedProperties();
        }
    }

    // ---------- Atalhos e formatação (100% compatível com HotKeyText.cs v1.3.0) ----------

    public static string AtalhoPadraoDoIndice(int i)
    {
        if (i < 0) return string.Empty;
        if (i < 9) return (i + 1).ToString();
        if (i == 9) return "0";
        if (i < 36) return ((char)('A' + i - 10)).ToString();
        return string.Empty;
    }

    public string ObterAtalhoItem(int i)
    {
        if (i >= 0 && i < Config.Textos.Count)
        {
            var a = (Config.Textos[i].Atalho ?? string.Empty).Trim();
            if (a.Length > 0) return a;
        }
        return AtalhoPadraoDoIndice(i);
    }

    public static string NomeDaTeclaBase(uint vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return ((char)('0' + (vk - 0x60))).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x20 => "Espaço",
            0x09 => "Tab",
            0xBD or 0x6D => "-",
            0xBB or 0x6B => "=",
            0xBC => ",",
            0xBE or 0x6E => ".",
            0xBF or 0x6F => "/",
            0xBA => ";",
            0xDB => "[",
            0xDD => "]",
            _ => string.Empty
        };
    }

    public static string FormatarCombinacao(bool ctrl, bool alt, bool shift, uint vk)
    {
        var baseKey = NomeDaTeclaBase(vk);
        if (string.IsNullOrEmpty(baseKey)) return string.Empty;
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        sb.Append(baseKey);
        return sb.ToString();
    }

    public static bool EhModificador(uint vk)
        => vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14;

    public int IndicePorAtalho(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return -1;
        for (var i = 0; i < Config.Textos.Count; i++)
        {
            var a = ObterAtalhoItem(i);
            if (string.Equals(a, combo.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public void AssignShortcut(int index, string combo)
    {
        if (index < 0 || index >= Config.Textos.Count)
            return;

        var normalized = (combo ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(normalized))
        {
            for (var i = 0; i < Config.Textos.Count; i++)
            {
                if (i != index && string.Equals(ObterAtalhoItem(i), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    Config.Textos[i].Atalho = AtalhoPadraoDoIndice(i);
                }
            }
        }

        Config.Textos[index].Atalho = normalized;
        ReindexItems();
        Save();
    }

    public static string TituloDe(TextoItem t)
    {
        if (!string.IsNullOrWhiteSpace(t.Titulo))
            return t.Titulo.Trim();
        if (t.OcultarTexto)
            return "(sem título)";
        var c = (t.Conteudo ?? string.Empty).Trim();
        if (c.Length == 0)
            return "(sem título)";
        var q = c.IndexOfAny(['\r', '\n']);
        return q > 0 ? c[..q] : c;
    }

    public static string ResumoDe(TextoItem t)
    {
        if (t.OcultarTexto)
            return "••••••••";
        var c = (t.Conteudo ?? string.Empty).Trim();
        if (c.Length == 0)
            return "(vazio)";
        if (c.Length > 300)
            c = c[..300];
        return c.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "  ↵  ").Replace("\t", " ");
    }

    // ---------- Detecção de WTS / Área de Trabalho Remota ----------

    public static bool EhClasseOuProcessoWtsOuRemoto(string? cls, string? procName)
    {
        var c = cls ?? string.Empty;
        if (c.Contains("TscShell", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("IHWindowClass", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("Transparent Windows Client", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var n = (procName ?? string.Empty).ToLowerInvariant();
        return n is "mstsc" or "msrdc" or "wfica32" or "cdviewer" or "vmware-view" or "anydesk" or "teamviewer";
    }

    public bool JanelaEhWtsOuRemota(IntPtr targetHwnd)
    {
        if (ForceWtsModeForTest)
            return true;

        try
        {
            var hwnd = targetHwnd != IntPtr.Zero ? targetHwnd : HotKeyNative.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            var sb = new StringBuilder(128);
            HotKeyNative.GetClassName(hwnd, sb, sb.Capacity);
            var cls = sb.ToString();
            if (EhClasseOuProcessoWtsOuRemoto(cls, null))
                return true;

            HotKeyNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
            {
                using var p = Process.GetProcessById((int)pid);
                if (EhClasseOuProcessoWtsOuRemoto(cls, p.ProcessName))
                    return true;
            }
        }
        catch { }

        return false;
    }

    // ---------- Hook de Teclado / Mouse e Popup do Scroll Lock ----------

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<HotKeyNative.KBDLLHOOKSTRUCT>(lParam);
            if (k.extra != SyntheticInputMark && !IsCapturingShortcut)
            {
                var msg = wParam.ToInt32();
                var down = msg is 0x0100 or 0x0104;
                var up = msg is 0x0101 or 0x0105;
                var vk = k.vkCode;

                if (vk == 0x91) // Scroll Lock
                {
                    if (down && !_scrollLockPressed)
                    {
                        _scrollLockPressed = true;
                        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(TogglePopup));
                    }
                    else if (up)
                    {
                        _scrollLockPressed = false;
                    }
                    return (IntPtr)1;
                }

                if (up && _swallowedKeys.Remove(vk))
                    return (IntPtr)1;
                if (down && _swallowedKeys.Contains(vk))
                    return (IntPtr)1;

                if (IsPopupOpen && down && !EhModificador(vk))
                {
                    _swallowedKeys.Add(vk);
                    var ctrl = (HotKeyNative.GetAsyncKeyState(0x11) & 0x8000) != 0;
                    var alt = (HotKeyNative.GetAsyncKeyState(0x12) & 0x8000) != 0;
                    var shift = (HotKeyNative.GetAsyncKeyState(0x10) & 0x8000) != 0;
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => HandlePopupKey(vk, ctrl, alt, shift)));
                    return (IntPtr)1;
                }
            }
        }

        return HotKeyNative.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsPopupOpen && _popupWindow is not null)
        {
            var msg = wParam.ToInt32();
            if (msg is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                var m = Marshal.PtrToStructure<HotKeyNative.MSLLHOOKSTRUCT>(lParam);
                if (!_popupWindow.ContainsScreenPoint(m.pt.x, m.pt.y))
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ClosePopup));
                }
            }
        }

        return HotKeyNative.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    public void TogglePopup()
    {
        if (IsPopupOpen)
            ClosePopup();
        else
            OpenPopup();
    }

    public void OpenPopup()
    {
        if (_disposed)
            return;

        var fg = HotKeyNative.GetForegroundWindow();
        RecordExternalWindowIfValid(fg);

        if (ExplicitTargetWindowForTest != IntPtr.Zero && HotKeyNative.IsWindow(ExplicitTargetWindowForTest))
        {
            _popupTargetWindow = ExplicitTargetWindowForTest;
        }
        else if (fg != IntPtr.Zero && fg != _mainWindowHandle && fg != (_popupWindow?.Hwnd ?? IntPtr.Zero))
        {
            _popupTargetWindow = fg;
        }
        else if (_lastExternalTargetWindow != IntPtr.Zero && HotKeyNative.IsWindow(_lastExternalTargetWindow))
        {
            _popupTargetWindow = _lastExternalTargetWindow;
        }
        else
        {
            _popupTargetWindow = fg;
        }

        ReindexItems();
        _popupWindow ??= new QuickTextsPopupWindow(this);
        IsPopupOpen = true;
        _popupWindow.ShowPopup();
        EnableMouseHook();
    }

    public void ClosePopup()
    {
        IsPopupOpen = false;
        DisableMouseHook();
        _popupWindow?.HidePopup();
    }

    public void HandlePopupKey(uint vk, bool ctrl = false, bool alt = false, bool shift = false)
    {
        if (!IsPopupOpen)
            return;

        if (vk == 0x1B) // Esc
        {
            ClosePopup();
            return;
        }

        if (vk == 0x26) // Up
        {
            _popupWindow?.MoveHighlight(-1);
            return;
        }

        if (vk == 0x28) // Down
        {
            _popupWindow?.MoveHighlight(1);
            return;
        }

        if (vk == 0x0D) // Enter
        {
            if (_popupWindow is { HighlightedIndex: >= 0 } popup)
            {
                SelectAndInsert(popup.HighlightedIndex);
            }
            return;
        }

        var combo = FormatarCombinacao(ctrl, alt, shift, vk);
        var idx = IndicePorAtalho(combo);
        if (idx >= 0 && idx < Config.Textos.Count)
        {
            SelectAndInsert(idx);
        }
    }

    public void RequestManageFromPopup()
    {
        ClosePopup();
        ManageRequestedFromPopup?.Invoke();
    }

    public void SelectAndInsert(int index, IntPtr? overrideTargetWindow = null)
    {
        var targetHwnd = overrideTargetWindow
            ?? (ExplicitTargetWindowForTest != IntPtr.Zero ? ExplicitTargetWindowForTest : _popupTargetWindow);

        ClosePopup();

        if (index < 0 || index >= Config.Textos.Count)
            return;

        var texto = Config.Textos[index].Conteudo ?? string.Empty;
        if (texto.Length == 0)
            return;

        if (targetHwnd == IntPtr.Zero || !HotKeyNative.IsWindow(targetHwnd))
        {
            if (_lastExternalTargetWindow != IntPtr.Zero && HotKeyNative.IsWindow(_lastExternalTargetWindow))
                targetHwnd = _lastExternalTargetWindow;
            else
                targetHwnd = HotKeyNative.GetForegroundWindow();
        }

        InsertTextIntoTarget(texto, targetHwnd);
    }

    public void InsertTextIntoTarget(string texto, IntPtr targetHwnd)
    {
        if (string.IsNullOrEmpty(texto))
            return;

        if (targetHwnd != IntPtr.Zero && HotKeyNative.IsWindow(targetHwnd))
        {
            EnsureTargetWindowForeground(targetHwnd);
        }

        var isWts = JanelaEhWtsOuRemota(targetHwnd);
        if (Config.ModoColar || isWts)
        {
            Colar(texto, targetHwnd, isWts);
        }
        else if (SendInputInterceptorForTest is not null)
        {
            Digitar(texto, targetHwnd);
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ => Digitar(texto, targetHwnd));
        }
    }

    public static bool EnsureTargetWindowForeground(IntPtr targetHwnd, int maxWaitMs = 120)
    {
        if (targetHwnd == IntPtr.Zero || !HotKeyNative.IsWindow(targetHwnd))
            return false;

        var currentFg = HotKeyNative.GetForegroundWindow();
        if (currentFg == targetHwnd)
            return true;

        try
        {
            HotKeyNative.GetWindowThreadProcessId(targetHwnd, out var targetPid);
            if (targetPid != 0)
            {
                try { HotKeyNative.AllowSetForegroundWindow((int)targetPid); } catch { }
            }

            if (HotKeyNative.IsIconic(targetHwnd))
            {
                HotKeyNative.ShowWindow(targetHwnd, HotKeyNative.SW_RESTORE);
            }

            var currentThreadId = HotKeyNative.GetCurrentThreadId();
            var fgThreadId = currentFg != IntPtr.Zero
                ? HotKeyNative.GetWindowThreadProcessId(currentFg, out _)
                : 0u;

            var attached = false;
            try
            {
                if (fgThreadId != 0 && fgThreadId != currentThreadId)
                {
                    attached = HotKeyNative.AttachThreadInput(currentThreadId, fgThreadId, true);
                }

                HotKeyNative.BringWindowToTop(targetHwnd);
                HotKeyNative.SetForegroundWindow(targetHwnd);
            }
            finally
            {
                if (attached)
                {
                    HotKeyNative.AttachThreadInput(currentThreadId, fgThreadId, false);
                }
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                if (HotKeyNative.GetForegroundWindow() == targetHwnd)
                    return true;
                Thread.Sleep(10);
            }
        }
        catch { }

        return HotKeyNative.GetForegroundWindow() == targetHwnd;
    }

    private static HotKeyNative.INPUT CreateKeyInput(ushort vk, ushort scan, uint flags)
    {
        var input = new HotKeyNative.INPUT
        {
            type = 1
        };
        input.u.ki.wVk = vk;
        if (scan == 0 && vk != 0)
            scan = (ushort)HotKeyNative.MapVirtualKey(vk, 0);
        input.u.ki.wScan = scan;
        input.u.ki.dwFlags = flags;
        input.u.ki.extra = SyntheticInputMark;
        return input;
    }

    private static void ReleaseStuckModifiers(List<HotKeyNative.INPUT> list)
    {
        const uint KEYEVENTF_KEYUP = 2;
        if ((HotKeyNative.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            list.Add(CreateKeyInput(0x10, 0x2A, KEYEVENTF_KEYUP));
            list.Add(CreateKeyInput(0xA0, 0x2A, KEYEVENTF_KEYUP));
            list.Add(CreateKeyInput(0xA1, 0x36, KEYEVENTF_KEYUP));
        }
        if ((HotKeyNative.GetAsyncKeyState(0x11) & 0x8000) != 0)
        {
            list.Add(CreateKeyInput(0x11, 0x1D, KEYEVENTF_KEYUP));
            list.Add(CreateKeyInput(0xA2, 0x1D, KEYEVENTF_KEYUP));
        }
        if ((HotKeyNative.GetAsyncKeyState(0x12) & 0x8000) != 0)
        {
            list.Add(CreateKeyInput(0x12, 0x38, KEYEVENTF_KEYUP));
            list.Add(CreateKeyInput(0xA4, 0x38, KEYEVENTF_KEYUP));
        }
    }

    private void Digitar(string texto, IntPtr targetHwnd)
    {
        if (Interlocked.Exchange(ref _isTyping, 1) == 1)
            return;

        try
        {
            if (targetHwnd != IntPtr.Zero && SendInputInterceptorForTest is null)
            {
                EnsureTargetWindowForeground(targetHwnd, maxWaitMs: 80);
            }

            const uint KEYEVENTF_KEYUP = 2;
            const uint KEYEVENTF_UNICODE = 4;

            var inputs = new List<HotKeyNative.INPUT>();
            ReleaseStuckModifiers(inputs);

            foreach (var c in texto)
            {
                if (c == '\r')
                    continue;
                if (c == '\n')
                {
                    inputs.Add(CreateKeyInput(0x10, 0x2A, 0));
                    inputs.Add(CreateKeyInput(0x0D, 0x1C, 0));
                    inputs.Add(CreateKeyInput(0x0D, 0x1C, KEYEVENTF_KEYUP));
                    inputs.Add(CreateKeyInput(0x10, 0x2A, KEYEVENTF_KEYUP));
                }
                else if (c == '\t')
                {
                    inputs.Add(CreateKeyInput(0x09, 0x0F, 0));
                    inputs.Add(CreateKeyInput(0x09, 0x0F, KEYEVENTF_KEYUP));
                }
                else
                {
                    inputs.Add(CreateKeyInput(0, c, KEYEVENTF_UNICODE));
                    inputs.Add(CreateKeyInput(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                }
            }

            if (SendInputInterceptorForTest is not null)
            {
                SendInputInterceptorForTest(inputs);
                return;
            }

            HotKeyNative.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<HotKeyNative.INPUT>());
        }
        finally
        {
            _isTyping = 0;
        }
    }

    private void Colar(string texto, IntPtr targetHwnd, bool isWts)
    {
        var normalized = texto.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        string? previousText = null;
        var hadPreviousText = false;
        try
        {
            if (Clipboard.ContainsText())
            {
                previousText = Clipboard.GetText();
                hadPreviousText = true;
            }
        }
        catch { }

        try
        {
            Clipboard.SetDataObject(normalized, true);
        }
        catch
        {
            if (SendInputInterceptorForTest is not null)
                Digitar(normalized, targetHwnd);
            else
                ThreadPool.QueueUserWorkItem(_ => Digitar(normalized, targetHwnd));
            return;
        }

        void SendCtrlV()
        {
            if (SendInputInterceptorForTest is null)
            {
                if (targetHwnd != IntPtr.Zero)
                    EnsureTargetWindowForeground(targetHwnd, maxWaitMs: 80);
                Thread.Sleep(isWts ? 140 : 25);
            }

            var inputs = new List<HotKeyNative.INPUT>();
            ReleaseStuckModifiers(inputs);
            inputs.Add(CreateKeyInput(0x11, 0x1D, 0)); // Ctrl Down
            inputs.Add(CreateKeyInput(0x56, 0x2F, 0)); // V Down
            inputs.Add(CreateKeyInput(0x56, 0x2F, 2)); // V Up
            inputs.Add(CreateKeyInput(0x11, 0x1D, 2)); // Ctrl Up

            if (SendInputInterceptorForTest is not null)
            {
                SendInputInterceptorForTest(inputs);
                return;
            }

            HotKeyNative.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<HotKeyNative.INPUT>());
        }

        if (SendInputInterceptorForTest is not null)
        {
            SendCtrlV();
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ => SendCtrlV());
        }

        if (hadPreviousText && previousText is not null && SendInputInterceptorForTest is null)
        {
            var restoreTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(isWts ? 1400 : 750)
            };
            restoreTimer.Tick += (_, _) =>
            {
                restoreTimer.Stop();
                try { Clipboard.SetDataObject(previousText, true); } catch { }
            };
            restoreTimer.Start();
        }
    }

    private void EnableMouseHook()
    {
        if (_msHook == IntPtr.Zero && IsHookActive && _kbHook != IntPtr.Zero)
        {
            _msHook = HotKeyNative.SetWindowsHookEx(
                HotKeyNative.WH_MOUSE_LL,
                _msProc,
                HotKeyNative.GetModuleHandle(null),
                0);
        }
    }

    private void DisableMouseHook()
    {
        if (_msHook != IntPtr.Zero)
        {
            HotKeyNative.UnhookWindowsHookEx(_msHook);
            _msHook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Usado durante a atualização/migração de pacotes anteriores (ex.: 1.8.0 com dois executáveis)
    /// para encerrar qualquer processo HotKeyText.exe antigo na pasta de instalação antes de removê-lo.
    /// </summary>
    public static bool StopRunningInstancesInDirectory(
        string targetDirectory,
        out bool wasRunning,
        string instancePrefix = "",
        int timeoutMs = 5000)
    {
        wasRunning = false;
        var fullTargetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var targetExePath = Path.Combine(fullTargetDir, "HotKeyText.exe");

        var matchingProcesses = new List<Process>();
        foreach (var proc in Process.GetProcessesByName("HotKeyText"))
        {
            try
            {
                var procPath = TryGetProcessExecutablePath(proc);
                if (procPath is not null &&
                    string.Equals(Path.GetFullPath(procPath), targetExePath, StringComparison.OrdinalIgnoreCase))
                {
                    matchingProcesses.Add(proc);
                }
                else if (procPath is null && !string.IsNullOrEmpty(instancePrefix))
                {
                    matchingProcesses.Add(proc);
                }
                else
                {
                    proc.Dispose();
                }
            }
            catch
            {
                proc.Dispose();
            }
        }

        var shutdownEventName = (instancePrefix ?? string.Empty) + DefaultShutdownEventName;
        var signaledEvent = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(shutdownEventName, out var shutdownEvent))
            {
                using (shutdownEvent)
                {
                    shutdownEvent.Set();
                    signaledEvent = true;
                    wasRunning = true;
                }
            }
        }
        catch { }

        if (matchingProcesses.Count == 0 && !signaledEvent)
            return true;

        wasRunning = true;
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (var proc in matchingProcesses)
            {
                var remaining = Math.Max(100, timeoutMs - (int)sw.ElapsedMilliseconds);
                try
                {
                    if (!proc.HasExited)
                        proc.WaitForExit(remaining);
                }
                catch { }
            }

            foreach (var proc in matchingProcesses)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
                catch { }
            }

            return true;
        }
        finally
        {
            foreach (var proc in matchingProcesses)
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    private static string? TryGetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _wtsPollingTimer?.Stop();
            _wtsPollingTimer = null;
        }
        catch { }

        try
        {
            DisableMouseHook();
        }
        catch { }

        try
        {
            if (_kbHook != IntPtr.Zero)
            {
                HotKeyNative.UnhookWindowsHookEx(_kbHook);
                _kbHook = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            _popupWindow?.Close();
            _popupWindow = null;
        }
        catch { }

        try
        {
            if (_instanceMutex is not null)
            {
                if (_ownsMutex)
                {
                    try { _instanceMutex.ReleaseMutex(); } catch { }
                }
                _instanceMutex.Dispose();
                _instanceMutex = null;
                _ownsMutex = false;
            }
        }
        catch { }

        IsHookActive = false;
    }
}

/// <summary>
/// Janela flutuante sem ativação (WS_EX_NOACTIVATE) exibida ao pressionar Scroll Lock.
/// Permite escolher rapidamente um texto pelo atalho, setas + Enter ou clique sem roubar o foco da janela de destino.
/// </summary>
internal sealed class QuickTextsPopupWindow : Window
{
    private readonly HotKeyTextService _service;
    private readonly StackPanel _itemsPanel;
    private readonly List<Border> _rowBorders = [];

    public int HighlightedIndex { get; private set; } = -1;
    public IntPtr Hwnd { get; private set; } = IntPtr.Zero;

    public QuickTextsPopupWindow(HotKeyTextService service)
    {
        _service = service;
        Title = "HotKeyText — Textos rápidos";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");

        var outerBorder = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            MinWidth = 420,
            MaxWidth = 520
        };
        outerBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        outerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var rootStack = new StackPanel();

        // Cabeçalho
        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var badgeBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            Width = 24,
            Height = 24,
            Background = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "H",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(badgeBorder, Dock.Left);
        headerDock.Children.Add(badgeBorder);

        var titleText = new TextBlock
        {
            Text = "Textos rápidos (Scroll Lock)",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        headerDock.Children.Add(titleText);
        rootStack.Children.Add(headerDock);

        _itemsPanel = new StackPanel();
        rootStack.Children.Add(_itemsPanel);

        // Rodapé
        var footerBorder = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(0, 6, 0, 0)
        };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var footerDock = new DockPanel();
        var manageLink = new TextBlock
        {
            Text = "Gerenciar",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        manageLink.SetResourceReference(TextBlock.ForegroundProperty, "FocusBorderBrush");
        manageLink.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _service.RequestManageFromPopup();
        };
        DockPanel.SetDock(manageLink, Dock.Right);
        footerDock.Children.Add(manageLink);

        var hintText = new TextBlock
        {
            Text = "Atalho do texto, Enter ou clique para inserir • Esc fecha",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        footerDock.Children.Add(hintText);

        footerBorder.Child = footerDock;
        rootStack.Children.Add(footerBorder);

        outerBorder.Child = rootStack;
        Content = outerBorder;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = HotKeyNative.GetWindowLong(Hwnd, HotKeyNative.GWL_EXSTYLE);
        HotKeyNative.SetWindowLong(
            Hwnd,
            HotKeyNative.GWL_EXSTYLE,
            exStyle | HotKeyNative.WS_EX_NOACTIVATE | HotKeyNative.WS_EX_TOOLWINDOW | HotKeyNative.WS_EX_TOPMOST);

        if (HwndSource.FromHwnd(Hwnd) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotKeyNative.WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)HotKeyNative.MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    public void ShowPopup()
    {
        RebuildItemsList();

        var workArea = SystemParameters.WorkArea;
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = Math.Max(420, DesiredSize.Width);
        var h = Math.Max(140, DesiredSize.Height);

        Left = workArea.Left + (workArea.Width - w) / 2;
        Top = workArea.Top + (workArea.Height - h) / 2;

        Show();
        if (Hwnd != IntPtr.Zero)
        {
            HotKeyNative.SetWindowPos(
                Hwnd,
                HotKeyNative.HWND_TOPMOST,
                0, 0, 0, 0,
                HotKeyNative.SWP_NOMOVE | HotKeyNative.SWP_NOSIZE | HotKeyNative.SWP_NOACTIVATE | HotKeyNative.SWP_SHOWWINDOW);
        }
    }

    public void HidePopup()
    {
        if (IsVisible)
            Hide();
    }

    public void MoveHighlight(int delta)
    {
        var count = _service.Config.Textos.Count;
        if (count == 0)
            return;

        HighlightedIndex = HighlightedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : (HighlightedIndex + delta + count) % count;

        UpdateHighlightVisuals();
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        if (Hwnd == IntPtr.Zero || !IsVisible)
            return false;

        if (!HotKeyNative.GetWindowRect(Hwnd, out var rect))
            return false;

        return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    }

    private void RebuildItemsList()
    {
        _itemsPanel.Children.Clear();
        _rowBorders.Clear();

        var textos = _service.Config.Textos;
        if (textos.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "Nenhum texto cadastrado. Clique em Gerenciar.",
                Margin = new Thickness(4, 8, 4, 8),
                FontSize = 12
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            _itemsPanel.Children.Add(emptyText);
            HighlightedIndex = -1;
            return;
        }

        HighlightedIndex = 0;
        for (var i = 0; i < textos.Count; i++)
        {
            var itemIndex = i;
            var item = textos[i];
            var shortcut = _service.ObterAtalhoItem(i);

            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = Cursors.Hand
            };

            var dock = new DockPanel();
            var shortcutBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                MinWidth = 28,
                Height = 26,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                Child = new TextBlock
                {
                    Text = shortcut,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(shortcutBadge, Dock.Left);
            dock.Children.Add(shortcutBadge);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleBlock = new TextBlock
            {
                Text = HotKeyTextService.TituloDe(item),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");

            var previewBlock = new TextBlock
            {
                Text = HotKeyTextService.ResumoDe(item),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            previewBlock.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");

            textStack.Children.Add(titleBlock);
            textStack.Children.Add(previewBlock);
            dock.Children.Add(textStack);

            rowBorder.Child = dock;
            rowBorder.MouseEnter += (_, _) =>
            {
                HighlightedIndex = itemIndex;
                UpdateHighlightVisuals();
            };
            rowBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _service.SelectAndInsert(itemIndex);
            };

            _rowBorders.Add(rowBorder);
            _itemsPanel.Children.Add(rowBorder);
        }

        UpdateHighlightVisuals();
    }

    private void UpdateHighlightVisuals()
    {
        for (var i = 0; i < _rowBorders.Count; i++)
        {
            if (i == HighlightedIndex)
                _rowBorders[i].SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            else
                _rowBorders[i].Background = Brushes.Transparent;
        }
    }
}
