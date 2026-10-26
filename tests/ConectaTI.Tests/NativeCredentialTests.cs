using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ConectaTI.App.Security;

namespace ConectaTI.Tests;

public sealed class NativeCredentialTests
{
    [Fact]
    public void NativeCredentialLayoutMatchesWindowsX64AndUnicodeBufferSizes()
    {
        Assert.Equal(8,IntPtr.Size);
        Assert.Equal(80,Marshal.SizeOf<WindowsCredentialVaultInterop.CREDENTIAL>());
        Assert.Equal(8,Marshal.OffsetOf<WindowsCredentialVaultInterop.CREDENTIAL>("TargetName").ToInt32());
        Assert.Equal(32,Marshal.OffsetOf<WindowsCredentialVaultInterop.CREDENTIAL>("CredentialBlobSize").ToInt32());
        Assert.Equal(40,Marshal.OffsetOf<WindowsCredentialVaultInterop.CREDENTIAL>("CredentialBlob").ToInt32());
        Assert.Equal(72,Marshal.OffsetOf<WindowsCredentialVaultInterop.CREDENTIAL>("UserName").ToInt32());
        var nativeCalls=0;
        Assert.True(WindowsCredentialVaultInterop.WriteGenericSecret("ConectaTI:Unicode:synthetic","usuário","Ação 🔐",
            (ref WindowsCredentialVaultInterop.CREDENTIAL c,uint flags)=>
            {
                nativeCalls++;
                Assert.Equal((uint)System.Text.Encoding.Unicode.GetByteCount("Ação 🔐"),c.CredentialBlobSize);
                Assert.Equal("Ação 🔐",Marshal.PtrToStringUni(c.CredentialBlob,(int)c.CredentialBlobSize/2));
                Assert.Equal(2u,c.Persist); Assert.Equal(1u,c.Type);
                return true;
            }));
        Assert.Equal(1,nativeCalls);
        Assert.Throws<ArgumentException>(()=>WindowsCredentialVaultInterop.WriteGenericSecret("ConectaTI:InvalidLength","synthetic",new string('x',1281),
            (ref WindowsCredentialVaultInterop.CREDENTIAL c,uint flags)=>{nativeCalls++;return true;}));
        Assert.Equal(1,nativeCalls);
    }

    [Fact]
    public void CredWriteFailure_CapturesWin32EightBeforeCallerCanChangeLastError()
    {
        var exception = Assert.Throws<NativeCredentialWriteException>(() =>
            WindowsCredentialVaultInterop.WriteGenericSecret("ConectaTI:Test:never-written", "fictional-user", "fictional-secret",
                (ref WindowsCredentialVaultInterop.CREDENTIAL credential, uint flags) =>
                {
                    Assert.Equal(1u, credential.Type);
                    Assert.Equal(0u, flags);
                    Assert.Equal((uint)("fictional-secret".Length * 2), credential.CredentialBlobSize);
                    Marshal.SetLastPInvokeError(8);
                    return false;
                }));
        Marshal.SetLastPInvokeError(87);
        Assert.Equal(8, exception.NativeErrorCode);
        Assert.Equal("CredWriteW", exception.NativeApi);
        Assert.IsAssignableFrom<Win32Exception>(exception);
        Assert.Contains("WriteInternal", exception.StackTrace);
        Assert.DoesNotContain("fictional", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedFailure_IsPreservedInsteadOfBeingReplacedByStaleWin32Error(bool outOfMemory)
    {
        Exception expected = outOfMemory ? new OutOfMemoryException("injected managed allocation failure")
            : new IOException("injected managed failure");
        Marshal.SetLastPInvokeError(8);
        var actual = Record.Exception(() => WindowsCredentialVaultInterop.WriteGenericSecret(
            "ConectaTI:Test:never-written", "fictional-user", "fictional-secret",
            (ref WindowsCredentialVaultInterop.CREDENTIAL credential, uint flags) => throw expected));
        Assert.Same(expected, actual);
        Assert.False(actual is Win32Exception);
    }

    [Fact]
    public void RealWindowsGenericCredential_WriteReadDelete_UsesOnlyUniqueSyntheticTarget()
    {
        var target = "ConectaTI:Validation:" + Guid.NewGuid().ToString("N");
        const string secret = "Synthetic native roundtrip #2026";
        try
        {
            Assert.True(WindowsCredentialVaultInterop.WriteGenericSecret(target, "synthetic-user", secret));
            Assert.Equal(secret, WindowsCredentialVaultInterop.ReadGenericSecret(target));
        }
        finally { WindowsCredentialVaultInterop.DeleteGenericSecret(target); }
        Assert.Null(WindowsCredentialVaultInterop.ReadGenericSecret(target));
    }

    [Fact]
    public void RealCredWrite_InvalidTargetReportsItsActualNativeError()
    {
        // An empty target cannot create or overwrite any stored credential.
        var exception = Assert.Throws<NativeCredentialWriteException>(() =>
            WindowsCredentialVaultInterop.WriteGenericSecret("", "synthetic-user", "synthetic-secret"));
        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Equal("CredWriteW", exception.NativeApi);
    }
}
