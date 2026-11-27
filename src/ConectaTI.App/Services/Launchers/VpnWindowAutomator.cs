using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ConectaTI.App.Services.Launchers;

public enum VpnAutomationVendorKind
{
    None = 0,
    FortiClient = 1,
    OpenConnectGui = 2,
    OpenVpnGuiAuthDialog = 3,
    GenericGui = 4
}

public sealed record VpnUiAutomationOutcome(
    bool WindowFound,
    bool ProfileSelected,
    bool CredentialsFilled,
    bool ConnectInvoked,
    bool AlreadyConnected,
    string? DetailMessage = null);

/// <summary>
/// Restaura janelas minimizadas/ocultas na bandeja do Windows (System Tray) e automatiza de forma segura,
/// em thread de segundo plano com timeout curto, a seleção de perfil e o preenchimento opcional de
/// Usuário/Senha em clientes VPN gráficos (FortiClient 7.x Electron, OpenConnect GUI Qt, OpenVPN GUI).
/// </summary>
public sealed class VpnWindowAutomator
{
    private readonly AppLogger? _logger;

    public VpnWindowAutomator(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public static bool TryRestoreProcessWindow(IReadOnlyList<string> processNames, IReadOnlyList<string>? titleKeywords = null)
    {
        var hwnd = FindCandidateWindow(processNames, titleKeywords, includeHiddenWindows: true);
        if (hwnd == IntPtr.Zero)
            return false;

        return RestoreAndFocusWindow(hwnd);
    }

    public Task<VpnUiAutomationOutcome> AutomateAsync(
        VpnAutomationVendorKind kind,
        string? vpnProfileName,
        VpnLoginCredential? manualCredential,
        CancellationToken cancellationToken = default)
    {
        // Sempre executa chamadas System.Windows.Automation fora da thread STA/UI do WPF para evitar deadlocks COM.
        return Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            var ct = timeoutCts.Token;

            try
            {
                return kind switch
                {
                    VpnAutomationVendorKind.FortiClient => await AutomateFortiClientCoreAsync(vpnProfileName, manualCredential, ct),
                    VpnAutomationVendorKind.OpenConnectGui => await AutomateOpenConnectCoreAsync(vpnProfileName, manualCredential, ct),
                    VpnAutomationVendorKind.OpenVpnGuiAuthDialog => manualCredential is { HasAnyValue: true }
                        ? await AutomateOpenVpnAuthDialogCoreAsync(manualCredential, ct)
                        : new VpnUiAutomationOutcome(false, false, false, false, false),
                    VpnAutomationVendorKind.GenericGui => manualCredential is { HasAnyValue: true }
                        ? await AutomateGenericLoginWindowCoreAsync(vpnProfileName, manualCredential, ct)
                        : new VpnUiAutomationOutcome(false, false, false, false, false),
                    _ => new VpnUiAutomationOutcome(false, false, false, false, false)
                };
            }
            catch (OperationCanceledException)
            {
                return new VpnUiAutomationOutcome(false, false, false, false, false);
            }
            catch (Exception ex)
            {
                _logger?.Warn("VPN.UIA", $"Automação assistida não concluída ({kind}): {ex.GetType().Name}");
                return new VpnUiAutomationOutcome(false, false, false, false, false);
            }
        }, cancellationToken);
    }

    private static async Task<VpnUiAutomationOutcome> AutomateFortiClientCoreAsync(
        string? vpnProfileName,
        VpnLoginCredential? manualCredential,
        CancellationToken ct)
    {
        string[] fortiProcs = ["FortiClientConsole", "FortiClient"];
        string[] fortiTitles = ["FortiClient"];

        IntPtr hwnd = IntPtr.Zero;
        AutomationElement? root = null;
        var controlsReady = false;

        // 1. Aguarda a janela do FortiClient abrir e a árvore de acessibilidade do Electron/Chromium popular
        for (var attempt = 0; attempt < 20 && !ct.IsCancellationRequested; attempt++)
        {
            hwnd = FindCandidateWindow(fortiProcs, fortiTitles, includeHiddenWindows: attempt < 4);
            if (hwnd != IntPtr.Zero)
            {
                RestoreAndFocusWindow(hwnd);
                try
                {
                    root = AutomationElement.FromHandle(hwnd);
                    // Consultar descendentes envia WM_GETOBJECT ao Chromium, ativando a árvore de acessibilidade
                    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    var combos = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
                    if (buttons.Count > 0 && (edits.Count > 0 || combos.Count > 0))
                    {
                        controlsReady = true;
                        break;
                    }
                }
                catch { }
            }

            await Task.Delay(250, ct);
        }

        if (hwnd == IntPtr.Zero || root is null)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        if (!controlsReady)
            return new VpnUiAutomationOutcome(true, false, false, false, false);

        // 2. Verifica se já existe uma VPN conectada ("Desconectar" / "Disconnect") para nunca desconectar por engano
        if (FindButtonByKeywords(root, ["desconectar", "disconnect"]) is not null)
        {
            return new VpnUiAutomationOutcome(
                WindowFound: true,
                ProfileSelected: false,
                CredentialsFilled: false,
                ConnectInvoked: false,
                AlreadyConnected: true,
                DetailMessage: L.Text("O FortiClient já possui uma VPN conectada."));
        }

        // 3. Seleciona o perfil em "Nome da VPN" (ComboBox) se informado
        var profileSelected = false;
        if (!string.IsNullOrWhiteSpace(vpnProfileName))
        {
            profileSelected = await TrySelectProfileInComboBoxAsync(root, vpnProfileName.Trim(), ct);
            if (profileSelected)
                await Task.Delay(220, ct);
        }

        // 4. Preenche Usuário e Senha SOMENTE se informados no ConectaTI e se o respectivo campo estiver vazio no FortiClient
        var credsFilled = false;
        var (userEdit, passEdit) = FindUsernameAndPasswordEdits(root);

        if (manualCredential is not null)
        {
            if (!string.IsNullOrWhiteSpace(manualCredential.FormattedUsername) && userEdit is not null)
            {
                var existingUser = TryGetElementText(userEdit);
                if (string.IsNullOrWhiteSpace(existingUser))
                {
                    if (TrySetElementValue(hwnd, userEdit, manualCredential.FormattedUsername))
                        credsFilled = true;
                }
            }

            if (!string.IsNullOrEmpty(manualCredential.Password) && passEdit is not null)
            {
                var existingPass = TryGetElementText(passEdit);
                if (string.IsNullOrEmpty(existingPass))
                {
                    if (TrySetElementValue(hwnd, passEdit, manualCredential.Password))
                        credsFilled = true;
                }
            }
        }

        if (credsFilled)
            await Task.Delay(120, ct);

        // 5. Clica automaticamente em "Conectar" quando os campos necessários estiverem prontos
        var connectInvoked = false;
        var connectBtn = FindButtonByKeywords(root, ["conectar", "connect", "login", "entrar"], excludeKeywords: ["desconectar", "disconnect"]);
        if (connectBtn is not null && connectBtn.Current.IsEnabled)
        {
            var finalUser = userEdit is not null ? TryGetElementText(userEdit) : "ok";
            // Se não há campo de usuário visível (ex.: IPsec por certificado/SAML) ou o usuário já está preenchido, aciona Conectar
            if (userEdit is null || !string.IsNullOrWhiteSpace(finalUser))
            {
                connectInvoked = TryInvokeButton(connectBtn);
            }
        }

        return new VpnUiAutomationOutcome(
            WindowFound: true,
            ProfileSelected: profileSelected,
            CredentialsFilled: credsFilled,
            ConnectInvoked: connectInvoked,
            AlreadyConnected: false);
    }

    private static async Task<VpnUiAutomationOutcome> AutomateOpenConnectCoreAsync(
        string? vpnProfileName,
        VpnLoginCredential? manualCredential,
        CancellationToken ct)
    {
        string[] ocProcs = ["openconnect-gui"];
        string[] ocTitles = ["OpenConnect"];

        IntPtr hwnd = IntPtr.Zero;
        AutomationElement? root = null;
        for (var attempt = 0; attempt < 16 && !ct.IsCancellationRequested; attempt++)
        {
            hwnd = FindCandidateWindow(ocProcs, ocTitles, includeHiddenWindows: true);
            if (hwnd != IntPtr.Zero)
            {
                RestoreAndFocusWindow(hwnd);
                try
                {
                    root = AutomationElement.FromHandle(hwnd);
                    if (root is not null)
                        break;
                }
                catch { }
            }
            await Task.Delay(200, ct);
        }

        if (hwnd == IntPtr.Zero || root is null)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        if (FindButtonByKeywords(root, ["disconnect", "desconectar"]) is { Current.IsEnabled: true })
        {
            return new VpnUiAutomationOutcome(true, false, false, false, true, L.Text("O OpenConnect GUI já está conectado."));
        }

        var profileSelected = false;
        if (!string.IsNullOrWhiteSpace(vpnProfileName))
        {
            profileSelected = await TrySelectProfileInComboBoxAsync(root, vpnProfileName.Trim(), ct);
            if (profileSelected)
                await Task.Delay(150, ct);
        }

        var connectBtn = FindButtonByKeywords(root, ["connect", "conectar"], excludeKeywords: ["disconnect", "desconectar"]);
        var connectInvoked = connectBtn is { Current.IsEnabled: true } && TryInvokeButton(connectBtn);

        var credsFilled = false;
        if (connectInvoked && manualCredential is { HasAnyValue: true })
        {
            // OpenConnect GUI abre diálogos modais sequenciais para Username e Password quando não salvos
            for (var step = 0; step < 16 && !ct.IsCancellationRequested; step++)
            {
                await Task.Delay(250, ct);
                var modalHwnd = FindModalDialogForProcesses(ocProcs, mainHwndToIgnore: hwnd);
                if (modalHwnd == IntPtr.Zero)
                    continue;

                try
                {
                    var modalRoot = AutomationElement.FromHandle(modalHwnd);
                    var (uEdit, pEdit) = FindUsernameAndPasswordEdits(modalRoot);
                    var filledThisDialog = false;

                    if (pEdit is not null && !string.IsNullOrEmpty(manualCredential.Password))
                    {
                        if (string.IsNullOrEmpty(TryGetElementText(pEdit)))
                            filledThisDialog = TrySetElementValue(modalHwnd, pEdit, manualCredential.Password);
                    }
                    else if (uEdit is not null && !string.IsNullOrWhiteSpace(manualCredential.FormattedUsername))
                    {
                        if (string.IsNullOrWhiteSpace(TryGetElementText(uEdit)))
                            filledThisDialog = TrySetElementValue(modalHwnd, uEdit, manualCredential.FormattedUsername);
                    }

                    if (filledThisDialog)
                    {
                        credsFilled = true;
                        var okBtn = FindButtonByKeywords(modalRoot, ["ok", "connect", "conectar", "login"]);
                        if (okBtn is not null)
                            TryInvokeButton(okBtn);
                    }
                }
                catch { }
            }
        }

        return new VpnUiAutomationOutcome(true, profileSelected, credsFilled, connectInvoked, false);
    }

    private static async Task<VpnUiAutomationOutcome> AutomateOpenVpnAuthDialogCoreAsync(
        VpnLoginCredential manualCredential,
        CancellationToken ct)
    {
        string[] ovpnProcs = ["openvpn-gui"];

        // Monitora apenas se o OpenVPN GUI abrir o diálogo de autenticação (caso o .ovpn não tenha senha salva)
        for (var attempt = 0; attempt < 18 && !ct.IsCancellationRequested; attempt++)
        {
            await Task.Delay(250, ct);
            var dlgHwnd = FindCandidateWindow(ovpnProcs, ["OpenVPN", "Authentication", "Autenticação", "Login"], includeHiddenWindows: false);
            if (dlgHwnd == IntPtr.Zero)
                continue;

            try
            {
                var dlgRoot = AutomationElement.FromHandle(dlgHwnd);
                var (userEdit, passEdit) = FindUsernameAndPasswordEdits(dlgRoot);
                if (userEdit is null && passEdit is null)
                    continue;

                RestoreAndFocusWindow(dlgHwnd);
                var filled = false;

                if (userEdit is not null && !string.IsNullOrWhiteSpace(manualCredential.FormattedUsername))
                {
                    if (string.IsNullOrWhiteSpace(TryGetElementText(userEdit)))
                    {
                        if (TrySetElementValue(dlgHwnd, userEdit, manualCredential.FormattedUsername))
                            filled = true;
                    }
                }

                if (passEdit is not null && !string.IsNullOrEmpty(manualCredential.Password))
                {
                    if (string.IsNullOrEmpty(TryGetElementText(passEdit)))
                    {
                        if (TrySetElementValue(dlgHwnd, passEdit, manualCredential.Password))
                            filled = true;
                    }
                }

                var okBtn = FindButtonByKeywords(dlgRoot, ["ok", "conectar", "connect", "login"]);
                var invoked = okBtn is { Current.IsEnabled: true } && TryInvokeButton(okBtn);
                return new VpnUiAutomationOutcome(true, true, filled, invoked, false);
            }
            catch { }
        }

        return new VpnUiAutomationOutcome(false, false, false, false, false);
    }

    private static async Task<VpnUiAutomationOutcome> AutomateGenericLoginWindowCoreAsync(
        string? vpnProfileName,
        VpnLoginCredential manualCredential,
        CancellationToken ct)
    {
        await Task.Delay(450, ct);
        var fg = Win32WindowNative.GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        try
        {
            var root = AutomationElement.FromHandle(fg);
            var profileSelected = !string.IsNullOrWhiteSpace(vpnProfileName) &&
                                  await TrySelectProfileInComboBoxAsync(root, vpnProfileName.Trim(), ct);
            var (userEdit, passEdit) = FindUsernameAndPasswordEdits(root);
            var filled = false;

            if (userEdit is not null && !string.IsNullOrWhiteSpace(manualCredential.FormattedUsername) &&
                string.IsNullOrWhiteSpace(TryGetElementText(userEdit)))
            {
                filled |= TrySetElementValue(fg, userEdit, manualCredential.FormattedUsername);
            }

            if (passEdit is not null && !string.IsNullOrEmpty(manualCredential.Password) &&
                string.IsNullOrEmpty(TryGetElementText(passEdit)))
            {
                filled |= TrySetElementValue(fg, passEdit, manualCredential.Password);
            }

            var invoked = false;
            if (filled)
            {
                var connectBtn = FindButtonByKeywords(root, ["conectar", "connect", "login", "ok"], excludeKeywords: ["desconectar", "disconnect"]);
                if (connectBtn is { Current.IsEnabled: true })
                    invoked = TryInvokeButton(connectBtn);
            }

            return new VpnUiAutomationOutcome(true, profileSelected, filled, invoked, false);
        }
        catch
        {
            return new VpnUiAutomationOutcome(false, false, false, false, false);
        }
    }

    private static async Task<bool> TrySelectProfileInComboBoxAsync(AutomationElement root, string targetProfile, CancellationToken ct)
    {
        var combos = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
        if (combos.Count == 0)
            return false;

        var combo = combos[0];
        var currentVal = TryGetElementText(combo);
        if (ProfileNamesMatch(currentVal, targetProfile))
            return true;

        ExpandCollapsePattern? expandPattern = null;
        try
        {
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawExpand))
            {
                expandPattern = (ExpandCollapsePattern)rawExpand;
                expandPattern.Expand();
            }
            else if (combo.TryGetCurrentPattern(InvokePattern.Pattern, out var rawInvoke))
            {
                ((InvokePattern)rawInvoke).Invoke();
            }
            else
            {
                combo.SetFocus();
            }
        }
        catch { }

        await Task.Delay(180, ct);

        // Procura ListItem na janela principal e, se o popup do Electron/Qt for top-level separado, nos filhos diretos do Desktop
        var matchedItem = FindMatchingListItem(root, targetProfile) ?? FindMatchingListItemInPopupWindows(targetProfile);
        if (matchedItem is not null)
        {
            var selected = false;
            try
            {
                if (matchedItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPat))
                {
                    ((SelectionItemPattern)selPat).Select();
                    selected = true;
                }
            }
            catch { }

            if (!selected)
            {
                try
                {
                    if (matchedItem.TryGetCurrentPattern(InvokePattern.Pattern, out var invPat))
                    {
                        ((InvokePattern)invPat).Invoke();
                        selected = true;
                    }
                }
                catch { }
            }

            try { expandPattern?.Collapse(); } catch { }
            if (selected)
                return true;
        }

        try { expandPattern?.Collapse(); } catch { }
        return false;
    }

    private static AutomationElement? FindMatchingListItem(AutomationElement scopeRoot, string targetProfile)
    {
        try
        {
            var items = scopeRoot.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));

            AutomationElement? stemCandidate = null;
            var stemCount = 0;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var name = (item.Current.Name ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                if (name.Equals(targetProfile, StringComparison.OrdinalIgnoreCase) ||
                    VpnClientLauncherService.CompactNormalize(name).Equals(VpnClientLauncherService.CompactNormalize(targetProfile), StringComparison.Ordinal))
                {
                    return item;
                }

                if (ProfileNamesMatch(name, targetProfile))
                {
                    stemCandidate = item;
                    stemCount++;
                }
            }

            if (stemCount == 1)
                return stemCandidate;
        }
        catch { }

        return null;
    }

    private static AutomationElement? FindMatchingListItemInPopupWindows(string targetProfile)
    {
        try
        {
            var topWindows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)));

            for (var i = 0; i < topWindows.Count; i++)
            {
                var w = topWindows[i];
                var cls = w.Current.ClassName ?? string.Empty;
                if (!cls.Contains("Chrome", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("Qt", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("ComboLBox", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var found = FindMatchingListItem(w, targetProfile);
                if (found is not null)
                    return found;
            }
        }
        catch { }

        return null;
    }

    private static bool ProfileNamesMatch(string? candidate, string target)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(target))
            return false;

        if (candidate.Trim().Equals(target.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var normCand = VpnClientLauncherService.CompactNormalize(candidate);
        var normTarget = VpnClientLauncherService.CompactNormalize(target);
        if (normCand.Length > 0 && normCand.Equals(normTarget, StringComparison.Ordinal))
            return true;

        var stemCand = VpnClientLauncherService.ExtractProfileStem(candidate);
        var stemTarget = VpnClientLauncherService.ExtractProfileStem(target);
        return stemCand.Length >= 3 && stemCand.Equals(stemTarget, StringComparison.Ordinal);
    }

    private static (AutomationElement? UserEdit, AutomationElement? PassEdit) FindUsernameAndPasswordEdits(AutomationElement root)
    {
        AutomationElement? userEdit = null;
        AutomationElement? passEdit = null;
        var nonPasswordEdits = new List<AutomationElement>();

        try
        {
            var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (!edit.Current.IsEnabled || edit.Current.IsOffscreen)
                    continue;

                var nameNorm = VpnClientLauncherService.CompactNormalize(edit.Current.Name);
                if (edit.Current.IsPassword ||
                    nameNorm.Contains("senha", StringComparison.Ordinal) ||
                    nameNorm.Contains("password", StringComparison.Ordinal) ||
                    nameNorm.Contains("contrase", StringComparison.Ordinal))
                {
                    passEdit ??= edit;
                }
                else
                {
                    nonPasswordEdits.Add(edit);
                    if (nameNorm.Contains("usuario", StringComparison.Ordinal) ||
                        nameNorm.Contains("user", StringComparison.Ordinal) ||
                        nameNorm.Contains("login", StringComparison.Ordinal))
                    {
                        userEdit ??= edit;
                    }
                }
            }

            if (userEdit is null && nonPasswordEdits.Count > 0)
                userEdit = nonPasswordEdits[0];
        }
        catch { }

        return (userEdit, passEdit);
    }

    private static AutomationElement? FindButtonByKeywords(
        AutomationElement root,
        IReadOnlyList<string> includeKeywords,
        IReadOnlyList<string>? excludeKeywords = null)
    {
        try
        {
            var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            for (var i = 0; i < buttons.Count; i++)
            {
                var btn = buttons[i];
                if (btn.Current.IsOffscreen)
                    continue;

                var norm = VpnClientLauncherService.CompactNormalize(btn.Current.Name);
                if (norm.Length == 0)
                    continue;

                if (excludeKeywords is not null && excludeKeywords.Any(ex => norm.Contains(ex, StringComparison.Ordinal)))
                    continue;

                if (includeKeywords.Any(inc => norm.Equals(inc, StringComparison.Ordinal) || norm.Contains(inc, StringComparison.Ordinal)))
                    return btn;
            }
        }
        catch { }

        return null;
    }

    private static string TryGetElementText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawVal))
                return ((ValuePattern)rawVal).Current.Value ?? string.Empty;

            if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var rawSel))
            {
                var selected = ((SelectionPattern)rawSel).Current.GetSelection();
                if (selected.Length > 0)
                    return selected[0].Current.Name ?? string.Empty;
            }
        }
        catch { }

        return string.Empty;
    }

    private static bool TrySetElementValue(IntPtr windowHwnd, AutomationElement element, string value)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawVal))
            {
                var valPattern = (ValuePattern)rawVal;
                if (!valPattern.Current.IsReadOnly)
                {
                    valPattern.SetValue(value);
                    return true;
                }
            }
        }
        catch { }

        // Fallback seguro via Unicode SendInput somente se o próprio controle estiver com foco ativo na janela alvo
        try
        {
            RestoreAndFocusWindow(windowHwnd);
            element.SetFocus();
            Thread.Sleep(60);
            if (Win32WindowNative.GetForegroundWindow() == windowHwnd)
            {
                SendUnicodeString(value);
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryInvokeButton(AutomationElement button)
    {
        try
        {
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var rawInvoke))
            {
                ((InvokePattern)rawInvoke).Invoke();
                return true;
            }
        }
        catch { }

        return false;
    }

    private static IntPtr FindCandidateWindow(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string>? titleKeywords,
        bool includeHiddenWindows)
    {
        var targetPids = new HashSet<uint>();
        foreach (var name in processNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try { if (!proc.HasExited) targetPids.Add((uint)proc.Id); } catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        if (targetPids.Count == 0 && (titleKeywords is null || titleKeywords.Count == 0))
            return IntPtr.Zero;

        IntPtr visibleMatch = IntPtr.Zero;
        IntPtr hiddenMatch = IntPtr.Zero;

        Win32WindowNative.EnumWindows((hwnd, _) =>
        {
            Win32WindowNative.GetWindowThreadProcessId(hwnd, out var pid);
            var pidMatches = targetPids.Contains(pid);
            if (!pidMatches && targetPids.Count > 0)
                return true;

            var title = Win32WindowNative.GetWindowTitle(hwnd);
            var cls = Win32WindowNative.GetWindowClassName(hwnd);

            if (cls is "MSCTFIME UI" or "IME" or "DummyDWMListenerWindow" or "GDI+ Hook Window Class")
                return true;

            var titleMatches = titleKeywords is null || titleKeywords.Count == 0 ||
                               titleKeywords.Any(kw => title.Contains(kw, StringComparison.OrdinalIgnoreCase));

            if (!pidMatches && !titleMatches)
                return true;

            // Para janelas ocultas na bandeja, exige título válido para não exibir janelas internas invisíveis de mensagem
            var isVisible = Win32WindowNative.IsWindowVisible(hwnd);
            if (isVisible)
            {
                if (title.Length > 0 || cls.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) || cls.Contains("Qt", StringComparison.OrdinalIgnoreCase))
                {
                    if (titleMatches || visibleMatch == IntPtr.Zero)
                        visibleMatch = hwnd;
                    if (titleMatches && title.Length > 0)
                        return false;
                }
            }
            else if (includeHiddenWindows && title.Length > 0 && titleMatches)
            {
                hiddenMatch = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return visibleMatch != IntPtr.Zero ? visibleMatch : hiddenMatch;
    }

    private static IntPtr FindModalDialogForProcesses(IReadOnlyList<string> processNames, IntPtr mainHwndToIgnore)
    {
        var pids = new HashSet<uint>();
        foreach (var name in processNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (!p.HasExited) pids.Add((uint)p.Id); } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        if (pids.Count == 0)
            return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        Win32WindowNative.EnumWindows((hwnd, _) =>
        {
            if (hwnd == mainHwndToIgnore || !Win32WindowNative.IsWindowVisible(hwnd))
                return true;

            Win32WindowNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid))
                return true;

            var title = Win32WindowNative.GetWindowTitle(hwnd);
            var cls = Win32WindowNative.GetWindowClassName(hwnd);
            if (cls is "MSCTFIME UI" or "IME")
                return true;

            if (title.Length > 0)
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static bool RestoreAndFocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            Win32WindowNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
                Win32WindowNative.AllowSetForegroundWindow((int)pid);

            if (!Win32WindowNative.IsWindowVisible(hwnd))
                Win32WindowNative.ShowWindow(hwnd, Win32WindowNative.SW_SHOW);

            if (Win32WindowNative.IsIconic(hwnd))
                Win32WindowNative.ShowWindow(hwnd, Win32WindowNative.SW_RESTORE);

            var currentFg = Win32WindowNative.GetForegroundWindow();
            var currentThread = Win32WindowNative.GetCurrentThreadId();
            var fgThread = currentFg != IntPtr.Zero ? Win32WindowNative.GetWindowThreadProcessId(currentFg, out _) : 0;

            var attached = false;
            try
            {
                if (fgThread != 0 && fgThread != currentThread)
                    attached = Win32WindowNative.AttachThreadInput(currentThread, fgThread, true);

                Win32WindowNative.BringWindowToTop(hwnd);
                Win32WindowNative.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    Win32WindowNative.AttachThreadInput(currentThread, fgThread, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SendUnicodeString(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new List<Win32WindowNative.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Win32WindowNative.CreateUnicodeKey(ch, keyUp: false));
            inputs.Add(Win32WindowNative.CreateUnicodeKey(ch, keyUp: true));
        }

        var arr = inputs.ToArray();
        Win32WindowNative.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32WindowNative.INPUT>());
    }

    private static class Win32WindowNative
    {
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static string GetWindowTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            return GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        public static string GetWindowClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(128);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        public static INPUT CreateUnicodeKey(char ch, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
