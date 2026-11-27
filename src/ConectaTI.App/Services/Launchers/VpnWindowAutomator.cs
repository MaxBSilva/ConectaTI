using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace ConectaTI.App.Services.Launchers;

public enum VpnAutomationVendorKind
{
    None = 0,
    FortiClient = 1,
    OpenConnectGui = 2,
    OpenVpnGuiAuthDialog = 3,
    GenericGui = 4,
    WindowsRasphone = 5
}

public sealed record VpnUiAutomationOutcome(
    bool WindowFound,
    bool ProfileSelected,
    bool CredentialsFilled,
    bool ConnectInvoked,
    bool AlreadyConnected,
    string? DetailMessage = null,
    bool ElevationDeclined = false,
    string? SelectedProfileName = null);

/// <summary>
/// Restaura janelas minimizadas/ocultas na bandeja do Windows, desperta a árvore de acessibilidade do Electron/Chromium
/// (Chrome_RenderWidgetHostHWND), contorna de forma legítima o isolamento UIPI quando a VPN roda como Administrador
/// e seleciona o perfil + clica em Conectar sem sobrescrever credenciais ou certificados já salvos.
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
        IReadOnlyList<string> candidates = string.IsNullOrWhiteSpace(vpnProfileName)
            ? Array.Empty<string>()
            : [vpnProfileName.Trim()];

        return AutomateWithCandidatesAsync(
            kind,
            candidates,
            manualCredential,
            allowElevationEscalation: true,
            cancellationToken);
    }

    public Task<VpnUiAutomationOutcome> AutomateWithCandidatesAsync(
        VpnAutomationVendorKind kind,
        IReadOnlyList<string> profileCandidates,
        VpnLoginCredential? manualCredential,
        bool allowElevationEscalation = true,
        CancellationToken cancellationToken = default)
    {
        var normalizedCandidates = profileCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Sempre executa System.Windows.Automation fora da thread STA/UI do WPF para evitar deadlocks COM.
        return Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(allowElevationEscalation ? 15 : 8));
            var ct = timeoutCts.Token;

            try
            {
                return kind switch
                {
                    VpnAutomationVendorKind.FortiClient => await AutomateFortiClientCoreAsync(normalizedCandidates, manualCredential, allowElevationEscalation, ct),
                    VpnAutomationVendorKind.OpenConnectGui => await AutomateOpenConnectCoreAsync(normalizedCandidates, manualCredential, allowElevationEscalation, ct),
                    VpnAutomationVendorKind.WindowsRasphone => await AutomateWindowsRasphoneCoreAsync(manualCredential, allowElevationEscalation, ct),
                    VpnAutomationVendorKind.OpenVpnGuiAuthDialog => manualCredential is { HasAnyValue: true }
                        ? await AutomateOpenVpnAuthDialogCoreAsync(manualCredential, allowElevationEscalation, ct)
                        : new VpnUiAutomationOutcome(false, false, false, false, false),
                    VpnAutomationVendorKind.GenericGui => await AutomateGenericLoginWindowCoreAsync(normalizedCandidates, manualCredential, allowElevationEscalation, ct),
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

    private async Task<VpnUiAutomationOutcome> AutomateFortiClientCoreAsync(
        IReadOnlyList<string> profileCandidates,
        VpnLoginCredential? manualCredential,
        bool allowElevationEscalation,
        CancellationToken ct)
    {
        string[] fortiProcs = ["FortiClientConsole", "FortiClient"];
        string[] fortiTitles = ["FortiClient"];

        IntPtr hwnd = IntPtr.Zero;
        IReadOnlyList<AutomationElement> roots = Array.Empty<AutomationElement>();
        var controlsReady = false;

        // 1. Localiza a janela do FortiClient, verifica privilégio UIPI (escudo Administrador) e desperta a árvore Chromium
        for (var attempt = 0; attempt < 22 && !ct.IsCancellationRequested; attempt++)
        {
            hwnd = FindCandidateWindow(fortiProcs, fortiTitles, includeHiddenWindows: attempt < 4);
            if (hwnd != IntPtr.Zero)
            {
                // Se o FortiClient estiver rodando como Administrador e o ConectaTI não, delega ao helper elevado via Named Pipe
                if (allowElevationEscalation && VpnElevationBridge.IsWindowHigherElevationThanCurrentProcess(hwnd))
                {
                    return await VpnElevationBridge.RunElevatedAutomationViaHelperAsync(
                        VpnAutomationVendorKind.FortiClient,
                        profileCandidates,
                        manualCredential,
                        _logger,
                        ct);
                }

                RestoreAndFocusWindow(hwnd);
                roots = WakeAndCollectSearchRoots(hwnd, isChromiumBased: true);

                var buttons = FindAllAcrossRoots(roots, ControlType.Button);
                var combos = FindAllAcrossRoots(roots, ControlType.ComboBox);
                var edits = FindAllAcrossRoots(roots, ControlType.Edit);
                if (buttons.Count > 0 && (combos.Count > 0 || edits.Count > 0))
                {
                    controlsReady = true;
                    break;
                }
            }

            await Task.Delay(250, ct);
        }

        if (hwnd == IntPtr.Zero || roots.Count == 0)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        if (!controlsReady)
            return new VpnUiAutomationOutcome(true, false, false, false, false);

        // 2. Verifica se já existe uma VPN conectada ou conectando para nunca derrubar sessão ativa
        if (FindButtonByKeywords(roots, ["desconectar", "disconnect"]) is not null)
        {
            return new VpnUiAutomationOutcome(
                WindowFound: true,
                ProfileSelected: false,
                CredentialsFilled: false,
                ConnectInvoked: false,
                AlreadyConnected: true,
                DetailMessage: L.Text("O FortiClient já possui uma VPN conectada."));
        }

        // 3. Seleciona o perfil em "Nome da VPN" (ignorando explicitamente "Certificado do Cliente")
        var profileSelected = false;
        string? matchedProfile = null;
        if (profileCandidates.Count > 0)
        {
            (profileSelected, matchedProfile) = await TrySelectMatchingProfileInComboBoxAsync(roots, hwnd, profileCandidates, ct);
            if (profileSelected)
            {
                await Task.Delay(220, ct);
                roots = WakeAndCollectSearchRoots(hwnd, isChromiumBased: true);
            }
        }

        // 4. Preenche Usuário e Senha SOMENTE se informados no ConectaTI e se não estiverem salvos no próprio FortiClient
        var credsFilled = false;
        var (userEdit, passEdit) = FindUsernameAndPasswordEdits(roots);
        var existingUserBefore = userEdit is not null ? TryGetElementText(userEdit) : string.Empty;
        var userWasAlreadySavedInVpn = !string.IsNullOrWhiteSpace(existingUserBefore);
        var savePasswordCheckedInVpn = IsSavePasswordCheckBoxChecked(roots);

        if (manualCredential is not null)
        {
            if (!string.IsNullOrWhiteSpace(manualCredential.FormattedUsername) && userEdit is not null && !userWasAlreadySavedInVpn)
            {
                if (TrySetElementValue(hwnd, userEdit, manualCredential.FormattedUsername))
                    credsFilled = true;
            }

            // Se o FortiClient já tem Usuário preenchido + checkbox "Salvar Senha" marcado, preserva a senha salva do próprio FortiClient
            var skipPasswordBecauseAlreadySavedInVpn = userWasAlreadySavedInVpn && savePasswordCheckedInVpn;
            if (!skipPasswordBecauseAlreadySavedInVpn && !string.IsNullOrEmpty(manualCredential.Password) && passEdit is not null)
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

        // 5. Clica diretamente em "Conectar" (ou "SAML Login") quando o perfil está pronto
        var connectInvoked = false;
        var finalUser = userEdit is not null ? TryGetElementText(userEdit) : "ok";
        if (userEdit is null || !string.IsNullOrWhiteSpace(finalUser))
        {
            connectInvoked = await TryActivateConnectButtonAsync(
                hwnd,
                roots,
                isChromiumBased: true,
                includeKeywords: ["conectar", "connect", "samllogin", "login", "entrar"],
                excludeKeywords: ["desconectar", "disconnect", "cancelar", "cancel"],
                ct);
        }

        return new VpnUiAutomationOutcome(
            WindowFound: true,
            ProfileSelected: profileSelected,
            CredentialsFilled: credsFilled,
            ConnectInvoked: connectInvoked,
            AlreadyConnected: false,
            SelectedProfileName: matchedProfile);
    }

    private async Task<VpnUiAutomationOutcome> AutomateOpenConnectCoreAsync(
        IReadOnlyList<string> profileCandidates,
        VpnLoginCredential? manualCredential,
        bool allowElevationEscalation,
        CancellationToken ct)
    {
        string[] ocProcs = ["openconnect-gui"];
        string[] ocTitles = ["OpenConnect"];

        IntPtr hwnd = IntPtr.Zero;
        IReadOnlyList<AutomationElement> roots = Array.Empty<AutomationElement>();

        for (var attempt = 0; attempt < 18 && !ct.IsCancellationRequested; attempt++)
        {
            hwnd = FindCandidateWindow(ocProcs, ocTitles, includeHiddenWindows: true);
            if (hwnd != IntPtr.Zero)
            {
                if (allowElevationEscalation && VpnElevationBridge.IsWindowHigherElevationThanCurrentProcess(hwnd))
                {
                    return await VpnElevationBridge.RunElevatedAutomationViaHelperAsync(
                        VpnAutomationVendorKind.OpenConnectGui,
                        profileCandidates,
                        manualCredential,
                        _logger,
                        ct);
                }

                RestoreAndFocusWindow(hwnd);
                roots = WakeAndCollectSearchRoots(hwnd, isChromiumBased: false);
                if (roots.Count > 0)
                    break;
            }
            await Task.Delay(200, ct);
        }

        if (hwnd == IntPtr.Zero || roots.Count == 0)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        if (FindButtonByKeywords(roots, ["disconnect", "desconectar"]) is { Current.IsEnabled: true })
        {
            return new VpnUiAutomationOutcome(true, false, false, false, true, L.Text("O OpenConnect GUI já está conectado."));
        }

        var profileSelected = false;
        string? matchedProfile = null;
        if (profileCandidates.Count > 0)
        {
            (profileSelected, matchedProfile) = await TrySelectMatchingProfileInComboBoxAsync(roots, hwnd, profileCandidates, ct);
            if (profileSelected)
            {
                await Task.Delay(150, ct);
                roots = WakeAndCollectSearchRoots(hwnd, isChromiumBased: false);
            }
        }

        var connectInvoked = await TryActivateConnectButtonAsync(
            hwnd,
            roots,
            isChromiumBased: false,
            includeKeywords: ["connect", "conectar"],
            excludeKeywords: ["disconnect", "desconectar", "cancel", "cancelar"],
            ct);

        var credsFilled = false;
        if (connectInvoked && manualCredential is { HasAnyValue: true })
        {
            for (var step = 0; step < 16 && !ct.IsCancellationRequested; step++)
            {
                await Task.Delay(250, ct);
                var modalHwnd = FindModalDialogForProcesses(ocProcs, mainHwndToIgnore: hwnd);
                if (modalHwnd == IntPtr.Zero)
                    continue;

                try
                {
                    var modalRoots = WakeAndCollectSearchRoots(modalHwnd, isChromiumBased: false);
                    var (uEdit, pEdit) = FindUsernameAndPasswordEdits(modalRoots);
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
                        var okBtn = FindButtonByKeywords(modalRoots, ["ok", "connect", "conectar", "login"]);
                        if (okBtn is not null)
                            TryInvokeButton(okBtn);
                    }
                }
                catch { }
            }
        }

        return new VpnUiAutomationOutcome(true, profileSelected, credsFilled, connectInvoked, false, SelectedProfileName: matchedProfile);
    }

    private async Task<VpnUiAutomationOutcome> AutomateWindowsRasphoneCoreAsync(
        VpnLoginCredential? manualCredential,
        bool allowElevationEscalation,
        CancellationToken ct)
    {
        string[] rasProcs = ["rasphone"];
        for (var attempt = 0; attempt < 16 && !ct.IsCancellationRequested; attempt++)
        {
            await Task.Delay(220, ct);
            var hwnd = FindCandidateWindow(rasProcs, null, includeHiddenWindows: false);
            if (hwnd == IntPtr.Zero)
                continue;

            if (allowElevationEscalation && VpnElevationBridge.IsWindowHigherElevationThanCurrentProcess(hwnd))
            {
                return await VpnElevationBridge.RunElevatedAutomationViaHelperAsync(
                    VpnAutomationVendorKind.WindowsRasphone,
                    Array.Empty<string>(),
                    manualCredential,
                    _logger,
                    ct);
            }

            RestoreAndFocusWindow(hwnd);
            var roots = WakeAndCollectSearchRoots(hwnd, isChromiumBased: false);
            if (roots.Count == 0)
                continue;

            if (FindButtonByKeywords(roots, ["desconectar", "disconnect", "hangup"]) is { Current.IsEnabled: true })
            {
                return new VpnUiAutomationOutcome(true, true, false, false, true, L.Text("A VPN do Windows já está conectada."));
            }

            var filled = false;
            if (manualCredential is { HasAnyValue: true })
            {
                var (uEdit, pEdit) = FindUsernameAndPasswordEdits(roots);
                if (uEdit is not null && !string.IsNullOrWhiteSpace(manualCredential.FormattedUsername) &&
                    string.IsNullOrWhiteSpace(TryGetElementText(uEdit)))
                {
                    filled |= TrySetElementValue(hwnd, uEdit, manualCredential.FormattedUsername);
                }

                if (pEdit is not null && !string.IsNullOrEmpty(manualCredential.Password) &&
                    string.IsNullOrEmpty(TryGetElementText(pEdit)))
                {
                    filled |= TrySetElementValue(hwnd, pEdit, manualCredential.Password);
                }
            }

            var invoked = await TryActivateConnectButtonAsync(
                hwnd,
                roots,
                isChromiumBased: false,
                includeKeywords: ["conectar", "connect", "discar", "dial", "ok"],
                excludeKeywords: ["desconectar", "disconnect", "cancelar", "cancel"],
                ct);

            return new VpnUiAutomationOutcome(true, true, filled, invoked, false);
        }

        return new VpnUiAutomationOutcome(false, false, false, false, false);
    }

    private async Task<VpnUiAutomationOutcome> AutomateOpenVpnAuthDialogCoreAsync(
        VpnLoginCredential manualCredential,
        bool allowElevationEscalation,
        CancellationToken ct)
    {
        string[] ovpnProcs = ["openvpn-gui"];

        for (var attempt = 0; attempt < 18 && !ct.IsCancellationRequested; attempt++)
        {
            await Task.Delay(250, ct);
            var dlgHwnd = FindCandidateWindow(ovpnProcs, ["OpenVPN", "Authentication", "Autenticação", "Login"], includeHiddenWindows: false);
            if (dlgHwnd == IntPtr.Zero)
                continue;

            if (allowElevationEscalation && VpnElevationBridge.IsWindowHigherElevationThanCurrentProcess(dlgHwnd))
            {
                return await VpnElevationBridge.RunElevatedAutomationViaHelperAsync(
                    VpnAutomationVendorKind.OpenVpnGuiAuthDialog,
                    Array.Empty<string>(),
                    manualCredential,
                    _logger,
                    ct);
            }

            try
            {
                var dlgRoots = WakeAndCollectSearchRoots(dlgHwnd, isChromiumBased: false);
                var (userEdit, passEdit) = FindUsernameAndPasswordEdits(dlgRoots);
                if (userEdit is null && passEdit is null)
                    continue;

                RestoreAndFocusWindow(dlgHwnd);
                var filled = false;

                if (userEdit is not null && !string.IsNullOrWhiteSpace(manualCredential.FormattedUsername) &&
                    string.IsNullOrWhiteSpace(TryGetElementText(userEdit)))
                {
                    filled |= TrySetElementValue(dlgHwnd, userEdit, manualCredential.FormattedUsername);
                }

                if (passEdit is not null && !string.IsNullOrEmpty(manualCredential.Password) &&
                    string.IsNullOrEmpty(TryGetElementText(passEdit)))
                {
                    filled |= TrySetElementValue(dlgHwnd, passEdit, manualCredential.Password);
                }

                var okBtn = FindButtonByKeywords(dlgRoots, ["ok", "conectar", "connect", "login"]);
                var invoked = okBtn is { Current.IsEnabled: true } && TryInvokeButton(okBtn);
                return new VpnUiAutomationOutcome(true, true, filled, invoked, false);
            }
            catch { }
        }

        return new VpnUiAutomationOutcome(false, false, false, false, false);
    }

    private async Task<VpnUiAutomationOutcome> AutomateGenericLoginWindowCoreAsync(
        IReadOnlyList<string> profileCandidates,
        VpnLoginCredential? manualCredential,
        bool allowElevationEscalation,
        CancellationToken ct)
    {
        await Task.Delay(450, ct);
        var fg = Win32WindowNative.GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return new VpnUiAutomationOutcome(false, false, false, false, false);

        if (allowElevationEscalation && VpnElevationBridge.IsWindowHigherElevationThanCurrentProcess(fg))
        {
            return await VpnElevationBridge.RunElevatedAutomationViaHelperAsync(
                VpnAutomationVendorKind.GenericGui,
                profileCandidates,
                manualCredential,
                _logger,
                ct);
        }

        try
        {
            var roots = WakeAndCollectSearchRoots(fg, isChromiumBased: true);
            var profileSelected = false;
            string? matchedProfile = null;
            if (profileCandidates.Count > 0)
            {
                (profileSelected, matchedProfile) = await TrySelectMatchingProfileInComboBoxAsync(roots, fg, profileCandidates, ct);
                if (profileSelected)
                {
                    await Task.Delay(150, ct);
                    roots = WakeAndCollectSearchRoots(fg, isChromiumBased: true);
                }
            }

            var (userEdit, passEdit) = FindUsernameAndPasswordEdits(roots);
            var filled = false;

            if (manualCredential is not null)
            {
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
            }

            var invoked = false;
            if (profileSelected || filled)
            {
                invoked = await TryActivateConnectButtonAsync(
                    fg,
                    roots,
                    isChromiumBased: false,
                    includeKeywords: ["conectar", "connect", "login", "ok"],
                    excludeKeywords: ["desconectar", "disconnect", "cancelar", "cancel"],
                    ct);
            }

            return new VpnUiAutomationOutcome(true, profileSelected, filled, invoked, false, SelectedProfileName: matchedProfile);
        }
        catch
        {
            return new VpnUiAutomationOutcome(false, false, false, false, false);
        }
    }

    private static IReadOnlyList<AutomationElement> WakeAndCollectSearchRoots(IntPtr topHwnd, bool isChromiumBased)
    {
        var roots = new List<AutomationElement>();
        if (topHwnd == IntPtr.Zero)
            return roots;

        if (isChromiumBased)
        {
            var renderHwnds = new List<IntPtr>();
            Win32WindowNative.EnumChildWindows(topHwnd, (childHwnd, _) =>
            {
                var cls = Win32WindowNative.GetWindowClassName(childHwnd);
                if (cls.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
                {
                    renderHwnds.Add(childHwnd);
                }
                return true;
            }, IntPtr.Zero);

            WakeSingleHwndAccessibility(topHwnd);
            foreach (var rHwnd in renderHwnds)
            {
                WakeSingleHwndAccessibility(rHwnd);
                try
                {
                    var renderElem = AutomationElement.FromHandle(rHwnd);
                    if (renderElem is not null)
                        roots.Add(renderElem);
                }
                catch { }
            }
        }

        try
        {
            var topElem = AutomationElement.FromHandle(topHwnd);
            if (topElem is not null)
                roots.Add(topElem);
        }
        catch { }

        return roots;
    }

    private static void WakeSingleHwndAccessibility(IntPtr hwnd)
    {
        try
        {
            // 1. Envia o sinal honey-pot ID=1 que o Chromium usa para detectar leitores de tela / UIA
            Win32WindowNative.SendMessageTimeout(
                hwnd,
                Win32WindowNative.WM_GETOBJECT,
                IntPtr.Zero,
                new IntPtr(1),
                Win32WindowNative.SMTO_ABORTIFHUNG,
                200,
                out _);

            // 2. Requisita OBJID_CLIENT via oleacc.dll para ativar a árvore completa do documento HTML
            var iid = Win32WindowNative.IID_IAccessible;
            var hr = Win32WindowNative.AccessibleObjectFromWindow(
                hwnd,
                Win32WindowNative.OBJID_CLIENT,
                ref iid,
                out var accObj);

            if (hr >= 0 && accObj is not null && Marshal.IsComObject(accObj))
            {
                Marshal.ReleaseComObject(accObj);
            }
        }
        catch { }
    }

    private static List<AutomationElement> FindAllAcrossRoots(IReadOnlyList<AutomationElement> roots, ControlType controlType)
    {
        var result = new List<AutomationElement>();
        var seenRects = new HashSet<string>(StringComparer.Ordinal);
        var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, controlType);

        foreach (var root in roots)
        {
            try
            {
                var found = root.FindAll(TreeScope.Descendants, cond);
                for (var i = 0; i < found.Count; i++)
                {
                    var elem = found[i];
                    var key = GetElementDedupKey(elem);
                    if (seenRects.Add(key))
                        result.Add(elem);
                }
            }
            catch { }
        }

        return result;
    }

    private static string GetElementDedupKey(AutomationElement elem)
    {
        try
        {
            var rid = elem.GetRuntimeId();
            if (rid is { Length: > 0 })
                return string.Join(".", rid);
        }
        catch { }

        try
        {
            var r = elem.Current.BoundingRectangle;
            return $"{elem.Current.Name}|{Math.Round(r.Left)}|{Math.Round(r.Top)}|{Math.Round(r.Width)}|{Math.Round(r.Height)}";
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static AutomationElement? FindVpnProfileComboBox(IReadOnlyList<AutomationElement> roots)
    {
        var combos = FindAllAcrossRoots(roots, ControlType.ComboBox);
        if (combos.Count == 0)
            return null;

        var valid = new List<(AutomationElement Elem, int Priority, double Top)>();
        foreach (var combo in combos)
        {
            try
            {
                if (!combo.Current.IsEnabled || combo.Current.IsOffscreen)
                    continue;

                var labelNorm = VpnClientLauncherService.CompactNormalize($"{combo.Current.Name} {combo.Current.AutomationId} {combo.Current.HelpText}");
                // Jamais tocar no seletor de Certificado do Cliente (ex.: FortiClient com certificado instalado)
                if (labelNorm.Contains("certificado", StringComparison.Ordinal) ||
                    labelNorm.Contains("certificate", StringComparison.Ordinal) ||
                    labelNorm.Contains("idioma", StringComparison.Ordinal) ||
                    labelNorm.Contains("language", StringComparison.Ordinal))
                {
                    continue;
                }

                var isExplicitVpnName =
                    labelNorm.Contains("nomedavpn", StringComparison.Ordinal) ||
                    labelNorm.Contains("vpnname", StringComparison.Ordinal) ||
                    labelNorm.Contains("nombredevpn", StringComparison.Ordinal) ||
                    labelNorm.Contains("perfil", StringComparison.Ordinal) ||
                    labelNorm.Contains("profile", StringComparison.Ordinal) ||
                    labelNorm.Contains("tunnel", StringComparison.Ordinal) ||
                    labelNorm.Contains("tunel", StringComparison.Ordinal) ||
                    labelNorm.Contains("server", StringComparison.Ordinal) ||
                    labelNorm.Contains("gateway", StringComparison.Ordinal);

                var rect = combo.Current.BoundingRectangle;
                var top = double.IsInfinity(rect.Top) || double.IsNaN(rect.Top) ? 99999 : rect.Top;
                valid.Add((combo, isExplicitVpnName ? 0 : 1, top));
            }
            catch { }
        }

        if (valid.Count == 0)
            return null;

        return valid
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Top)
            .First()
            .Elem;
    }

    private static async Task<(bool Selected, string? MatchedName)> TrySelectMatchingProfileInComboBoxAsync(
        IReadOnlyList<AutomationElement> roots,
        IntPtr topHwnd,
        IReadOnlyList<string> profileCandidates,
        CancellationToken ct)
    {
        var combo = FindVpnProfileComboBox(roots);
        if (combo is null)
            return (false, null);

        var currentText = GetComboBoxSelectedText(combo);
        foreach (var candidate in profileCandidates)
        {
            if (ProfileNamesMatch(currentText, candidate))
                return (true, string.IsNullOrWhiteSpace(currentText) ? candidate : currentText);
        }

        // Tenta abrir o ComboBox e selecionar o item correspondente na lista popup
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

        await Task.Delay(200, ct);

        foreach (var candidate in profileCandidates)
        {
            var matchedItem = FindMatchingListItemAcrossRoots(roots, candidate)
                              ?? FindMatchingListItemInPopupWindows(candidate);
            if (matchedItem is not null)
            {
                var itemName = (matchedItem.Current.Name ?? candidate).Trim();
                var selected = TrySelectOrInvokeListItem(matchedItem);
                try { expandPattern?.Collapse(); } catch { }

                if (selected)
                    return (true, itemName);
            }
        }

        try { expandPattern?.Collapse(); } catch { }

        // Fallback seguro por teclado para elementos <select> HTML do Electron/Qt quando a janela alvo está em primeiro plano
        try
        {
            RestoreAndFocusWindow(topHwnd);
            combo.SetFocus();
            await Task.Delay(60, ct);

            if (Win32WindowNative.GetForegroundWindow() == topHwnd)
            {
                // Fecha eventual popup aberto para navegar diretamente nas opções com Home / Seta para Baixo
                SendVirtualKey(Win32WindowNative.VK_ESCAPE);
                await Task.Delay(40, ct);
                combo.SetFocus();

                SendVirtualKey(Win32WindowNative.VK_HOME);
                await Task.Delay(55, ct);

                var prevText = string.Empty;
                var unchangedSteps = 0;

                for (var step = 0; step < 45 && !ct.IsCancellationRequested; step++)
                {
                    var val = GetComboBoxSelectedText(combo);
                    foreach (var candidate in profileCandidates)
                    {
                        if (ProfileNamesMatch(val, candidate))
                        {
                            return (true, string.IsNullOrWhiteSpace(val) ? candidate : val);
                        }
                    }

                    if (step > 0 && string.Equals(val, prevText, StringComparison.Ordinal))
                    {
                        unchangedSteps++;
                        if (unchangedSteps >= 2)
                            break;
                    }
                    else
                    {
                        unchangedSteps = 0;
                    }

                    prevText = val;
                    SendVirtualKey(Win32WindowNative.VK_DOWN);
                    await Task.Delay(50, ct);
                }
            }
        }
        catch { }

        return (false, null);
    }

    private static bool TrySelectOrInvokeListItem(AutomationElement item)
    {
        try
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPat))
            {
                ((SelectionItemPattern)selPat).Select();
                return true;
            }
        }
        catch { }

        try
        {
            if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var invPat))
            {
                ((InvokePattern)invPat).Invoke();
                return true;
            }
        }
        catch { }

        return false;
    }

    private static string GetComboBoxSelectedText(AutomationElement combo)
    {
        var direct = TryGetElementText(combo);
        if (!string.IsNullOrWhiteSpace(direct))
            return direct.Trim();

        try
        {
            var children = combo.FindAll(
                TreeScope.Children,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));

            for (var i = 0; i < children.Count; i++)
            {
                var ch = children[i];
                if (ch.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel) &&
                    ((SelectionItemPattern)sel).Current.IsSelected &&
                    !string.IsNullOrWhiteSpace(ch.Current.Name))
                {
                    return ch.Current.Name.Trim();
                }
            }

            if (children.Count == 1 && !string.IsNullOrWhiteSpace(children[0].Current.Name))
                return children[0].Current.Name.Trim();
        }
        catch { }

        return string.Empty;
    }

    private static AutomationElement? FindMatchingListItemAcrossRoots(IReadOnlyList<AutomationElement> roots, string targetProfile)
    {
        foreach (var root in roots)
        {
            var found = FindMatchingListItem(root, targetProfile);
            if (found is not null)
                return found;
        }
        return null;
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
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)));

            for (var i = 0; i < topWindows.Count; i++)
            {
                var w = topWindows[i];
                var cls = w.Current.ClassName ?? string.Empty;
                if (!cls.Contains("Chrome", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("Qt", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("ComboLBox", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("Popup", StringComparison.OrdinalIgnoreCase))
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

    private static bool IsSavePasswordCheckBoxChecked(IReadOnlyList<AutomationElement> roots)
    {
        var checkBoxes = FindAllAcrossRoots(roots, ControlType.CheckBox);
        foreach (var cb in checkBoxes)
        {
            try
            {
                var norm = VpnClientLauncherService.CompactNormalize(cb.Current.Name);
                if (norm.Contains("salvarsenha", StringComparison.Ordinal) ||
                    norm.Contains("savepassword", StringComparison.Ordinal) ||
                    norm.Contains("guardarcontrase", StringComparison.Ordinal) ||
                    norm.Contains("remember", StringComparison.Ordinal) ||
                    norm.Contains("lembrar", StringComparison.Ordinal))
                {
                    if (cb.TryGetCurrentPattern(TogglePattern.Pattern, out var rawToggle))
                    {
                        return ((TogglePattern)rawToggle).Current.ToggleState == ToggleState.On;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private static (AutomationElement? UserEdit, AutomationElement? PassEdit) FindUsernameAndPasswordEdits(IReadOnlyList<AutomationElement> roots)
    {
        AutomationElement? userEdit = null;
        AutomationElement? passEdit = null;
        var nonPasswordEdits = new List<AutomationElement>();

        var edits = FindAllAcrossRoots(roots, ControlType.Edit);
        foreach (var edit in edits)
        {
            try
            {
                if (!edit.Current.IsEnabled || edit.Current.IsOffscreen)
                    continue;

                var nameNorm = VpnClientLauncherService.CompactNormalize($"{edit.Current.Name} {edit.Current.AutomationId}");
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
            catch { }
        }

        if (userEdit is null && nonPasswordEdits.Count > 0)
            userEdit = nonPasswordEdits[0];

        return (userEdit, passEdit);
    }

    private static AutomationElement? FindButtonByKeywords(
        IReadOnlyList<AutomationElement> roots,
        IReadOnlyList<string> includeKeywords,
        IReadOnlyList<string>? excludeKeywords = null)
    {
        var buttons = FindAllAcrossRoots(roots, ControlType.Button);
        AutomationElement? partialMatch = null;

        foreach (var btn in buttons)
        {
            try
            {
                if (!btn.Current.IsEnabled || btn.Current.IsOffscreen)
                    continue;

                var norm = VpnClientLauncherService.CompactNormalize(btn.Current.Name);
                if (norm.Length == 0)
                    continue;

                if (excludeKeywords is not null && excludeKeywords.Any(ex => norm.Contains(ex, StringComparison.Ordinal)))
                    continue;

                if (includeKeywords.Any(inc => norm.Equals(inc, StringComparison.Ordinal)))
                    return btn;

                if (partialMatch is null && includeKeywords.Any(inc => norm.Contains(inc, StringComparison.Ordinal)))
                    partialMatch = btn;
            }
            catch { }
        }

        return partialMatch;
    }

    private static async Task<bool> TryActivateConnectButtonAsync(
        IntPtr topHwnd,
        IReadOnlyList<AutomationElement> roots,
        bool isChromiumBased,
        IReadOnlyList<string> includeKeywords,
        IReadOnlyList<string> excludeKeywords,
        CancellationToken ct)
    {
        var connectBtn = FindButtonByKeywords(roots, includeKeywords, excludeKeywords);
        if (connectBtn is null)
            return false;

        // 1. Acionamento padrão via UI Automation InvokePattern
        var invoked = TryInvokeButton(connectBtn);
        if (invoked)
        {
            if (!isChromiumBased)
                return true;

            // Em Electron/Angular (FortiClient), confirma em 260ms se o botão "Conectar" iniciou a transição (mudou para "Cancelar" / "Desconectar")
            await Task.Delay(260, ct);
            var refreshedRoots = WakeAndCollectSearchRoots(topHwnd, isChromiumBased: true);
            if (FindButtonByKeywords(refreshedRoots, ["cancelar", "cancel", "desconectar", "disconnect"]) is not null ||
                FindButtonByKeywords(refreshedRoots, includeKeywords, excludeKeywords) is null)
            {
                return true;
            }

            connectBtn = FindButtonByKeywords(refreshedRoots, includeKeywords, excludeKeywords) ?? connectBtn;
        }

        // 2. Fallback por foco no botão + tecla Enter com janela validada em primeiro plano
        try
        {
            RestoreAndFocusWindow(topHwnd);
            connectBtn.SetFocus();
            await Task.Delay(60, ct);
            if (Win32WindowNative.GetForegroundWindow() == topHwnd)
            {
                SendVirtualKey(Win32WindowNative.VK_RETURN);
                await Task.Delay(220, ct);

                var refreshedAfterEnter = WakeAndCollectSearchRoots(topHwnd, isChromiumBased);
                if (FindButtonByKeywords(refreshedAfterEnter, ["cancelar", "cancel", "desconectar", "disconnect"]) is not null ||
                    FindButtonByKeywords(refreshedAfterEnter, includeKeywords, excludeKeywords) is null)
                {
                    return true;
                }

                connectBtn = FindButtonByKeywords(refreshedAfterEnter, includeKeywords, excludeKeywords) ?? connectBtn;
            }
        }
        catch { }

        // 3. Fallback cirúrgico de clique nas coordenadas do próprio botão (restaurando o cursor imediatamente)
        if (TryClickElementBoundsInsideWindow(topHwnd, connectBtn))
            return true;

        return invoked;
    }

    private static bool TryClickElementBoundsInsideWindow(IntPtr topHwnd, AutomationElement element)
    {
        try
        {
            RestoreAndFocusWindow(topHwnd);
            Thread.Sleep(50);
            if (Win32WindowNative.GetForegroundWindow() != topHwnd)
                return false;

            if (!Win32WindowNative.GetWindowRect(topHwnd, out var winRect))
                return false;

            Rect bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4 ||
                double.IsNaN(bounds.Left) || double.IsInfinity(bounds.Left))
            {
                return false;
            }

            var clickX = (int)Math.Round(bounds.Left + (bounds.Width / 2.0));
            var clickY = (int)Math.Round(bounds.Top + (bounds.Height / 2.0));

            // Garante que o ponto clicado está estritamente dentro da janela da VPN
            if (clickX <= winRect.Left || clickX >= winRect.Right ||
                clickY <= winRect.Top || clickY >= winRect.Bottom)
            {
                return false;
            }

            Win32WindowNative.GetCursorPos(out var savedCursor);
            try
            {
                Win32WindowNative.SetCursorPos(clickX, clickY);
                var mouseInputs = new[]
                {
                    Win32WindowNative.CreateMouseLeftButton(buttonUp: false),
                    Win32WindowNative.CreateMouseLeftButton(buttonUp: true)
                };
                Win32WindowNative.SendInput((uint)mouseInputs.Length, mouseInputs, Marshal.SizeOf<Win32WindowNative.INPUT>());
                return true;
            }
            finally
            {
                Win32WindowNative.SetCursorPos(savedCursor.X, savedCursor.Y);
            }
        }
        catch
        {
            return false;
        }
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

    private static void SendVirtualKey(ushort vk)
    {
        var arr = new[]
        {
            Win32WindowNative.CreateVirtualKey(vk, keyUp: false),
            Win32WindowNative.CreateVirtualKey(vk, keyUp: true)
        };
        Win32WindowNative.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32WindowNative.INPUT>());
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
        public const uint WM_GETOBJECT = 0x003D;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_HOME = 0x24;
        public const ushort VK_DOWN = 0x28;

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        public static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            int dwId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

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

        public static INPUT CreateVirtualKey(ushort vk, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        public static INPUT CreateMouseLeftButton(bool buttonUp)
        {
            return new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = buttonUp ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
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
