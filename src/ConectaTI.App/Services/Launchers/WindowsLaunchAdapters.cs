using System.Diagnostics;
using ConectaTI.App.Security;
namespace ConectaTI.App.Services.Launchers;

public interface IProcessRunner { int? Start(ProcessStartInfo startInfo); }
public sealed class SystemProcessRunner : IProcessRunner
{
    public int? Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process?.Id;
    }
}
internal sealed class DelegateProcessRunner(Func<ProcessStartInfo, int?> start) : IProcessRunner
{
    public int? Start(ProcessStartInfo startInfo) => start(startInfo);
}
internal interface IRdpPasswordEncoder { string Encode(string password); }
internal sealed class WindowsRdpPasswordEncoder : IRdpPasswordEncoder
{
    public string Encode(string password) => WindowsCredentialVaultInterop.EncryptRdpPassword51Hex(password);
}
