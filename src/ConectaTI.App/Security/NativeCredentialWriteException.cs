using System.ComponentModel;

namespace ConectaTI.App.Security;

public class NativeCredentialException(string nativeApi, int nativeErrorCode) : Win32Exception(nativeErrorCode)
{
    public string NativeApi { get; }=nativeApi;
}

public sealed class NativeCredentialWriteException(int nativeErrorCode)
    : NativeCredentialException("CredWriteW",nativeErrorCode);
