namespace ConectaTI.App.Services.Diagnostics;

public interface INetworkConnectivityProbe
{
    Task<NetworkDiagnosticResult> TestConnectivityAsync(string hostInput, int port, int timeoutMs = 4000, CancellationToken cancellationToken = default);
}
