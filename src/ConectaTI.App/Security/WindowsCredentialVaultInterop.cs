using System.Runtime.InteropServices;
using System.Text;
using ConectaTI.App.Helpers;

namespace ConectaTI.App.Security;

public record RdpCredentialInspection(
    bool Exists,
    string TargetName,
    string UserName,
    string Comment,
    bool IsManagedByConectaTI,
    string? ManagedConnectionId);

public record RdpCredentialProvisionResult(
    bool Success,
    IReadOnlyList<string> ProvisionedTargets,
    IReadOnlyList<string> PreservedPreExistingTargets,
    string NoticeMessage);

/// <summary>
/// Integração nativa com o Gerenciador de Credenciais do Windows (advapi32.dll: CredReadW, CredWriteW, CredDeleteW, CredEnumerateW)
/// e proteção DPAPI nativa de sessão RDP (crypt32.dll: CryptProtectData com descrição "psw").
/// Preserva credenciais preexistentes do Windows sem substituí-las ou apagá-las silenciosamente,
/// e controla o ciclo de vida das credenciais criadas pelo ConectaTI.
/// </summary>
public static class WindowsCredentialVaultInterop
{
    public const string ManagedRdpCommentPrefix = "ConectaTI-Managed-RDP:";

    internal delegate bool CredentialWriteCall(ref CREDENTIAL credential, uint flags);

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const uint CRED_PERSIST_SESSION = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, uint flags, out int count, out IntPtr pCredentials);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // =========================================================================
    // Destinos TERMSRV e Ciclo de Vida para o MSTSC (mstsc.exe)
    // =========================================================================

    /// <summary>
    /// Retorna os identificadores de destino TERMSRV consultados pelo mstsc.exe para o host e porta informados.
    /// Inclui o host normalizado (DNS, IPv4 ou IPv6) e, quando a porta for personalizada (!= 3389),
    /// também o formato com porta para compatibilidade com diferentes versões do cliente RDP.
    /// </summary>
    public static IReadOnlyList<string> GetRdpCredentialTargets(string host, int port)
    {
        var targets = new List<string>();
        var cleanHost = ConnectionValidator.StripIpv6Brackets(host);
        if (string.IsNullOrWhiteSpace(cleanHost))
            return targets;

        targets.Add($"TERMSRV/{cleanHost}");

        if (cleanHost.Contains(':'))
        {
            var bracketed = $"TERMSRV/[{cleanHost}]";
            if (!targets.Contains(bracketed, StringComparer.OrdinalIgnoreCase))
                targets.Add(bracketed);
        }

        if (port > 0 && port != 3389)
        {
            var withPort = $"TERMSRV/{ConnectionValidator.FormatHostAndPort(cleanHost, port)}";
            if (!targets.Contains(withPort, StringComparer.OrdinalIgnoreCase))
                targets.Add(withPort);
        }

        return targets;
    }

    public static RdpCredentialInspection InspectRdpCredentialTarget(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return new RdpCredentialInspection(false, string.Empty, string.Empty, string.Empty, false, null);

        if (!CredRead(targetName, CRED_TYPE_DOMAIN_PASSWORD, 0, out var credPtr) || credPtr == IntPtr.Zero)
            return new RdpCredentialInspection(false, targetName, string.Empty, string.Empty, false, null);

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var userName = cred.UserName != IntPtr.Zero ? Marshal.PtrToStringUni(cred.UserName) ?? string.Empty : string.Empty;
            var comment = cred.Comment != IntPtr.Zero ? Marshal.PtrToStringUni(cred.Comment) ?? string.Empty : string.Empty;

            var isManaged = comment.StartsWith(ManagedRdpCommentPrefix, StringComparison.Ordinal);
            var connId = isManaged ? comment[ManagedRdpCommentPrefix.Length..].Trim() : null;

            return new RdpCredentialInspection(true, targetName, userName, comment, isManaged, connId);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>
    /// Provisiona a credencial RDP no Gerenciador de Credenciais do Windows para uso pelo mstsc.exe,
    /// sem substituir silenciosamente credenciais preexistentes criadas fora do ConectaTI.
    /// </summary>
    public static RdpCredentialProvisionResult ProvisionRdpSessionCredentials(
        string connectionId,
        string host,
        int port,
        string effectiveUser,
        string password)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(password))
        {
            return new RdpCredentialProvisionResult(false, Array.Empty<string>(), Array.Empty<string>(), string.Empty);
        }

        var targets = GetRdpCredentialTargets(host, port);
        var provisioned = new List<string>();
        var preservedPreExisting = new List<string>();
        var comment = $"{ManagedRdpCommentPrefix}{connectionId}";

        foreach (var target in targets)
        {
            var existing = InspectRdpCredentialTarget(target);
            if (existing.Exists && !existing.IsManagedByConectaTI)
            {
                // Credencial preexistente no Windows (não criada pelo ConectaTI): NÃO sobrescreve nem apaga silenciosamente!
                preservedPreExisting.Add(target);
                continue;
            }

            if (WriteInternal(target, effectiveUser, password, comment, CRED_TYPE_DOMAIN_PASSWORD, CRED_PERSIST_SESSION))
            {
                provisioned.Add(target);
            }
        }

        string notice;
        if (preservedPreExisting.Count > 0 && provisioned.Count == 0)
        {
            notice = L.Format("Uma credencial preexistente do Windows para '{0}' foi preservada sem substituição silenciosa. A senha do cadastro foi incluída de forma criptografada no arquivo de sessão .rdp.", (preservedPreExisting[0]));
        }
        else if (preservedPreExisting.Count > 0)
        {
            notice = L.Format("Credencial preparada para o MSTSC (credencial preexistente em '{0}' preservada).", preservedPreExisting[0]);
        }
        else if (provisioned.Count == 0)
        {
            notice = L.Text("Não foi possível preparar a credencial temporária do MSTSC. O acesso foi preservado; a autenticação depende do cliente RDP.");
        }
        else
        {
            notice = L.Text("Credencial do cadastro disponibilizada ao MSTSC via Gerenciador de Credenciais do Windows (temporária de sessão).");
        }

        return new RdpCredentialProvisionResult(
            provisioned.Count > 0 || preservedPreExisting.Count > 0,
            provisioned,
            preservedPreExisting,
            notice);
    }

    /// <summary>
    /// Remove do Gerenciador de Credenciais do Windows apenas as credenciais TERMSRV gerenciadas pelo ConectaTI
    /// para o host/porta informados. Nunca apaga credenciais preexistentes do usuário no Windows.
    /// </summary>
    public static int ClearManagedRdpCredentialsForHost(string host, int port, string? onlyIfConnectionId = null)
    {
        var removedCount = 0;
        var targets = GetRdpCredentialTargets(host, port);

        foreach (var target in targets)
        {
            var info = InspectRdpCredentialTarget(target);
            if (!info.Exists || !info.IsManagedByConectaTI)
                continue;

            if (!string.IsNullOrWhiteSpace(onlyIfConnectionId) &&
                !string.Equals(info.ManagedConnectionId, onlyIfConnectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    /// Limpa todas as credenciais TERMSRV temporárias criadas pelo ConectaTI na sessão do Windows
    /// (por exemplo, ao bloquear ou fechar o aplicativo), preservando intactas quaisquer credenciais do usuário.
    /// </summary>
    public static int CleanupAllManagedRdpCredentials()
    {
        var removedCount = 0;
        if (!CredEnumerate("TERMSRV/*", 0, out var count, out var pCredentials) || pCredentials == IntPtr.Zero)
            return 0;

        try
        {
            var toDelete = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(pCredentials, i * IntPtr.Size);
                if (credPtr == IntPtr.Zero)
                    continue;

                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var target = cred.TargetName != IntPtr.Zero ? Marshal.PtrToStringUni(cred.TargetName) : null;
                var comment = cred.Comment != IntPtr.Zero ? Marshal.PtrToStringUni(cred.Comment) : null;

                if (!string.IsNullOrWhiteSpace(target) &&
                    !string.IsNullOrWhiteSpace(comment) &&
                    comment.StartsWith(ManagedRdpCommentPrefix, StringComparison.Ordinal))
                {
                    toDelete.Add(target);
                }
            }

            foreach (var target in toDelete)
            {
                if (CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0))
                    removedCount++;
            }
        }
        finally
        {
            CredFree(pCredentials);
        }

        return removedCount;
    }

    // =========================================================================
    // Criptografia DPAPI "password 51:b:" nativa para arquivos .rdp (CryptProtectData "psw")
    // =========================================================================

    /// <summary>
    /// Gera o hash criptografado DPAPI compatível com a propriedade "password 51:b:" do mstsc.exe
    /// (mesmo formato utilizado pelo AutoPuTTY e pelo cliente de Área de Trabalho Remota do Windows).
    /// O valor gerado nunca contém texto puro e só pode ser descriptografado pelo usuário Windows atual.
    /// </summary>
    public static string EncryptRdpPassword51Hex(string password)
    {
        if (string.IsNullOrEmpty(password))
            return string.Empty;

        var plainBytes = Encoding.Unicode.GetBytes(password);
        var inPtr = IntPtr.Zero;
        var outBlob = new DATA_BLOB();

        try
        {
            inPtr = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inPtr, plainBytes.Length);

            var inBlob = new DATA_BLOB
            {
                cbData = plainBytes.Length,
                pbData = inPtr
            };

            if (!CryptProtectData(
                    ref inBlob,
                    "psw",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob) || outBlob.pbData == IntPtr.Zero || outBlob.cbData <= 0)
            {
                return string.Empty;
            }

            var encryptedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, encryptedBytes, 0, outBlob.cbData);
            return Convert.ToHexString(encryptedBytes);
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
            if (inPtr != IntPtr.Zero)
            {
                for (var i = 0; i < plainBytes.Length; i++)
                    Marshal.WriteByte(inPtr, i, 0);
                Marshal.FreeHGlobal(inPtr);
            }
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    /// <summary>
    /// Descriptografa um valor "password 51:b:" (apenas para verificação em testes automatizados no mesmo usuário Windows).
    /// </summary>
    public static string DecryptRdpPassword51Hex(string hexValue)
    {
        if (string.IsNullOrWhiteSpace(hexValue))
            return string.Empty;

        var encryptedBytes = Convert.FromHexString(hexValue.Trim());
        var inPtr = IntPtr.Zero;
        var outBlob = new DATA_BLOB();

        try
        {
            inPtr = Marshal.AllocHGlobal(encryptedBytes.Length);
            Marshal.Copy(encryptedBytes, 0, inPtr, encryptedBytes.Length);

            var inBlob = new DATA_BLOB
            {
                cbData = encryptedBytes.Length,
                pbData = inPtr
            };

            if (!CryptUnprotectData(
                    ref inBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob) || outBlob.pbData == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(outBlob.pbData, outBlob.cbData / 2) ?? string.Empty;
        }
        finally
        {
            if (inPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }

    // =========================================================================
    // Cofre Genérico do ConectaTI no Windows Credential Manager
    // =========================================================================

    public static bool WriteGenericSecret(string targetName, string userName, string secret)
    {
        return WriteGenericSecret(targetName, userName, secret, CredWrite);
    }

    internal static bool WriteGenericSecret(string targetName, string userName, string secret, CredentialWriteCall write) =>
        WriteInternal(targetName, userName, secret, "ConectaTI Vault Secret", CRED_TYPE_GENERIC,
            CRED_PERSIST_LOCAL_MACHINE, throwNativeErrors: true, write);

    public static bool WriteRdpSessionCredential(string host, string effectiveUser, string password)
    {
        var res = ProvisionRdpSessionCredentials("session", host, 3389, effectiveUser, password);
        return res.Success;
    }

    public static string? ReadGenericSecret(string targetName)
    {
        if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error=Marshal.GetLastPInvokeError();
            if(error==1168) return null;
            throw new NativeCredentialException("CredReadW",error);
        }
        if(credPtr==IntPtr.Zero) throw new InvalidDataException("CredReadW returned an invalid buffer.");

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return string.Empty;

            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void DeleteGenericSecret(string targetName)
    {
        if (!CredDelete(targetName, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) // ERROR_NOT_FOUND is an idempotent successful cleanup.
                throw new System.ComponentModel.Win32Exception(error);
        }
    }

    private static bool WriteInternal(
        string targetName,
        string userName,
        string secret,
        string? comment,
        uint credType,
        uint persist,
        bool throwNativeErrors = false, CredentialWriteCall? write = null)
    {
        var byteCount = Encoding.Unicode.GetByteCount(secret ?? string.Empty);
        var targetPtr = IntPtr.Zero;
        var userPtr = IntPtr.Zero;
        var commentPtr = IntPtr.Zero;
        var blobPtr = IntPtr.Zero;

        try
        {
            var maxTarget=credType==CRED_TYPE_GENERIC ? 32767 : 337;
            if(targetName.Length>maxTarget || targetName.Contains('\0') || (userName?.Length ?? 0)>513 ||
                (userName?.Contains('\0') ?? false) || (comment?.Length ?? 0)>256 || byteCount>2560)
                throw new ArgumentException("Credential field exceeds its native format limit.");
            targetPtr = Marshal.StringToCoTaskMemUni(targetName);
            userPtr = Marshal.StringToCoTaskMemUni(userName ?? string.Empty);
            commentPtr = Marshal.StringToCoTaskMemUni(comment ?? string.Empty);
            blobPtr = Marshal.StringToCoTaskMemUni(secret ?? string.Empty);

            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = credType,
                TargetName = targetPtr,
                Comment = commentPtr,
                UserName = userPtr,
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob = blobPtr,
                Persist = persist
            };

            var success = (write ?? CredWrite)(ref credential, 0);
            // Capture before zeroing/freeing buffers or invoking any other native function.
            var nativeError = success ? 0 : Marshal.GetLastPInvokeError();
            if (!success && throwNativeErrors) throw new NativeCredentialWriteException(nativeError);
            return success;
        }
        catch when (!throwNativeErrors)
        {
            // Preserve the existing RDP fallback contract; generic writes keep their exception type.
            return false;
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                for (var i = 0; i < byteCount; i++)
                    Marshal.WriteByte(blobPtr, i, 0);
                Marshal.FreeCoTaskMem(blobPtr);
            }
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (commentPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(commentPtr);
        }
    }
}
