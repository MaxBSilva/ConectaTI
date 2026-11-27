using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConectaTI.App.Services.Launchers;

/// <summary>
/// Mensagem transitória em memória enviada exclusivamente via Named Pipe local para o processo auxiliar elevado.
/// ToString nunca expõe o campo Password.
/// </summary>
public sealed record VpnElevationAutomationRequest(
    VpnAutomationVendorKind Kind,
    IReadOnlyList<string> ProfileCandidates,
    string? Username,
    string? Domain,
    string? Password)
{
    public override string ToString() =>
        $"VpnElevationAutomationRequest(Kind={Kind}, Profiles=[{string.Join(", ", ProfileCandidates)}], User={Username}, Domain={Domain})";
}

/// <summary>
/// Detecta quando a janela de um cliente VPN (ex.: FortiClientConsole.exe) está rodando como Administrador
/// (High Integrity / UIPI) enquanto o ConectaTI está em nível padrão (Medium Integrity), e coordena um modo
/// auxiliar efêmero (--vpn-uia-helper) via Named Pipe local sem expor segredos em argumentos de linha de comando
/// e sem exigir requireAdministrator fixo no manifesto (preservando a inicialização com o Windows em HKCU\Run).
/// </summary>
public static class VpnElevationBridge
{
    public const string HelperCliFlag = "--vpn-uia-helper";

    private const uint SECURITY_MANDATORY_MEDIUM_RID = 0x00002000;
    private const uint SECURITY_MANDATORY_HIGH_RID = 0x00003000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_CANCELLED = 1223;

    public static bool IsCurrentProcessElevated()
    {
        if (Environment.IsPrivilegedProcess)
            return true;

        var rid = TryGetProcessIntegrityRid(NativeTokens.GetCurrentProcess(), isPseudoHandle: true);
        return rid.HasValue && rid.Value >= SECURITY_MANDATORY_HIGH_RID;
    }

    public static bool IsWindowHigherElevationThanCurrentProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var currentRid = TryGetProcessIntegrityRid(NativeTokens.GetCurrentProcess(), isPseudoHandle: true)
                         ?? (Environment.IsPrivilegedProcess ? SECURITY_MANDATORY_HIGH_RID : SECURITY_MANDATORY_MEDIUM_RID);

        // Se o próprio ConectaTI já está rodando como Administrador (High/System), não há bloqueio UIPI
        if (currentRid >= SECURITY_MANDATORY_HIGH_RID)
            return false;

        NativeTokens.GetWindowThreadProcessId(hwnd, out var targetPid);
        if (targetPid == 0)
            return false;

        var hProcess = NativeTokens.OpenProcess(NativeTokens.PROCESS_QUERY_LIMITED_INFORMATION, false, targetPid);
        if (hProcess == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        }

        try
        {
            var targetRid = TryGetProcessIntegrityRid(hProcess, isPseudoHandle: false);
            if (!targetRid.HasValue)
            {
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            }

            return targetRid.Value > currentRid;
        }
        finally
        {
            NativeTokens.CloseHandle(hProcess);
        }
    }

    public static string BuildSafeHelperArguments(string pipeName, string sessionNonce)
        => $"{HelperCliFlag} {pipeName} {sessionNonce}";

    public static async Task<VpnUiAutomationOutcome> RunElevatedAutomationViaHelperAsync(
        VpnAutomationVendorKind kind,
        IReadOnlyList<string> profileCandidates,
        VpnLoginCredential? manualCredential,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new VpnUiAutomationOutcome(true, false, false, false, false);

        var pipeName = $"ConectaTI_VpnUia_{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        await using var pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = BuildSafeHelperArguments(pipeName, nonce),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process? helperProcess = null;
        try
        {
            try
            {
                helperProcess = Process.Start(psi);
            }
            catch (Win32Exception w32) when (w32.NativeErrorCode == ERROR_CANCELLED)
            {
                return new VpnUiAutomationOutcome(
                    WindowFound: true,
                    ProfileSelected: false,
                    CredentialsFilled: false,
                    ConnectInvoked: false,
                    AlreadyConnected: false,
                    DetailMessage: L.Text("A janela da VPN está como Administrador. Aprove a confirmação ou use 'Mais ▾ → Executar como Administrador' para conectar em 1 clique."),
                    ElevationDeclined: true);
            }

            if (helperProcess is null)
                return new VpnUiAutomationOutcome(true, false, false, false, false);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(12));
            await pipeServer.WaitForConnectionAsync(connectCts.Token);

            using var reader = new StreamReader(pipeServer, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipeServer, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ioCts.CancelAfter(TimeSpan.FromSeconds(9));

            var clientNonce = await reader.ReadLineAsync(ioCts.Token);
            if (!string.Equals(clientNonce, nonce, StringComparison.Ordinal))
            {
                logger?.Warn("VPN.UIA", "Handshake inválido no pipe local do helper de VPN.");
                return new VpnUiAutomationOutcome(true, false, false, false, false);
            }

            var req = new VpnElevationAutomationRequest(
                Kind: kind,
                ProfileCandidates: profileCandidates,
                Username: manualCredential?.Username,
                Domain: manualCredential?.Domain,
                Password: manualCredential?.Password);

            await writer.WriteLineAsync(JsonSerializer.Serialize(req));

            var responseLine = await reader.ReadLineAsync(ioCts.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
                return new VpnUiAutomationOutcome(true, false, false, false, false);

            return JsonSerializer.Deserialize<VpnUiAutomationOutcome>(responseLine)
                   ?? new VpnUiAutomationOutcome(true, false, false, false, false);
        }
        catch (Exception ex)
        {
            logger?.Warn("VPN.UIA", $"Helper elevado de VPN não concluiu: {ex.GetType().Name}");
            return new VpnUiAutomationOutcome(true, false, false, false, false);
        }
        finally
        {
            helperProcess?.Dispose();
        }
    }

    public static async Task<bool> TryExecuteHelperModeAsync(string[] args)
    {
        var idx = Array.IndexOf(args, HelperCliFlag);
        if (idx < 0)
            return false;

        if (idx + 2 >= args.Length)
            return true;

        var pipeName = args[idx + 1];
        var nonce = args[idx + 2];
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(nonce))
            return true;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(4000, cts.Token);

            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(nonce);

            var reqJson = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(reqJson))
                return true;

            var req = JsonSerializer.Deserialize<VpnElevationAutomationRequest>(reqJson);
            if (req is null)
                return true;

            var candidateCred = new VpnLoginCredential(
                req.Username ?? string.Empty,
                req.Domain ?? string.Empty,
                req.Password ?? string.Empty);
            VpnLoginCredential? cred = candidateCred.HasAnyValue ? candidateCred : null;

            var automator = new VpnWindowAutomator();
            var outcome = await automator.AutomateWithCandidatesAsync(
                req.Kind,
                req.ProfileCandidates,
                cred,
                allowElevationEscalation: false,
                cancellationToken: cts.Token);

            await writer.WriteLineAsync(JsonSerializer.Serialize(outcome));
        }
        catch
        {
            // Encerra silenciosamente sem abrir interface gráfica
        }

        return true;
    }

    public static bool TryRestartAsAdministrator(string? explicitDataDirectory = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (!string.IsNullOrWhiteSpace(explicitDataDirectory))
            {
                psi.Arguments = $"--data-dir \"{explicitDataDirectory}\"";
            }

            using var started = Process.Start(psi);
            return started is not null;
        }
        catch (Win32Exception w32) when (w32.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static uint? TryGetProcessIntegrityRid(IntPtr processHandle, bool isPseudoHandle)
    {
        if (!NativeTokens.OpenProcessToken(processHandle, NativeTokens.TOKEN_QUERY, out var hToken) || hToken == IntPtr.Zero)
            return null;

        try
        {
            NativeTokens.GetTokenInformation(hToken, NativeTokens.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 4096)
                return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!NativeTokens.GetTokenInformation(hToken, NativeTokens.TokenIntegrityLevel, buffer, needed, out _))
                    return null;

                var til = Marshal.PtrToStructure<NativeTokens.TOKEN_MANDATORY_LABEL>(buffer);
                if (til.Label.Sid == IntPtr.Zero)
                    return null;

                var countPtr = NativeTokens.GetSidSubAuthorityCount(til.Label.Sid);
                if (countPtr == IntPtr.Zero)
                    return null;

                var count = Marshal.ReadByte(countPtr);
                if (count == 0)
                    return null;

                var ridPtr = NativeTokens.GetSidSubAuthority(til.Label.Sid, (uint)(count - 1));
                if (ridPtr == IntPtr.Zero)
                    return null;

                return (uint)Marshal.ReadInt32(ridPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeTokens.CloseHandle(hToken);
        }
    }

    private static class NativeTokens
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenIntegrityLevel = 25;

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            int TokenInformationClass,
            IntPtr TokenInformation,
            int TokenInformationLength,
            out int ReturnLength);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
