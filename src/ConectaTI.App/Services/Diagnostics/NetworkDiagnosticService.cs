using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConectaTI.App.Helpers;

namespace ConectaTI.App.Services.Diagnostics;

public record NetworkDiagnosticResult(
    bool DnsResolved,
    bool TcpPortReachable,
    IReadOnlyList<string> ResolvedAddresses,
    long DnsDurationMs,
    long TcpDurationMs,
    string SummaryTitle,
    string DetailedMessage);

/// <summary>
/// Executa testes opcionais de resolução DNS e conectividade TCP na porta configurada,
/// com suporte a timeout e cancelamento.
/// Diferencia explicitamente "porta acessível na rede" de "autenticação realizada".
/// </summary>
public class NetworkDiagnosticService
{
    public async Task<NetworkDiagnosticResult> TestConnectivityAsync(
        string hostInput,
        int port,
        int timeoutMs = 4000,
        CancellationToken cancellationToken = default)
    {
        if (!ConnectionValidator.IsValidHost(hostInput, out var cleanHost, out _))
        {
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                0,
                0,
                L.Text("Host inválido"),
                L.Text("O hostname ou endereço IP informado não possui um formato DNS, IPv4 ou IPv6 válido."));
        }

        if (!ConnectionValidator.IsValidPort(port))
        {
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                0,
                0,
                L.Text("Porta inválida"),
                L.Text("A porta deve estar entre 1 e 65535."));
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30000)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var dnsSw = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            if (IPAddress.TryParse(cleanHost, out var parsedIp))
            {
                addresses = [parsedIp];
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(cleanHost, linkedCts.Token);
            }
            dnsSw.Stop();
        }
        catch (OperationCanceledException)
        {
            dnsSw.Stop();
            var wasUserCancel = cancellationToken.IsCancellationRequested;
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                dnsSw.ElapsedMilliseconds,
                0,
                wasUserCancel ? L.Text("Teste cancelado") : L.Text("Timeout na resolução DNS"),
                wasUserCancel
                    ? L.Text("O diagnóstico de rede foi cancelado pelo usuário.")
                    : L.Format("A resolução DNS para '{0}' excedeu o tempo limite de {1} ms.", (cleanHost), (timeoutMs)));
        }
        catch (Exception ex)
        {
            dnsSw.Stop();
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                dnsSw.ElapsedMilliseconds,
                0,
                L.Text("Falha na resolução DNS"),
                L.Format("Não foi possível resolver o endereço '{0}': {1}", (cleanHost), (ex.Message)));
        }

        if (addresses.Length == 0)
        {
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                dnsSw.ElapsedMilliseconds,
                0,
                L.Text("DNS sem registros"),
                L.Format("O host '{0}' não retornou nenhum endereço IPv4 ou IPv6.", (cleanHost)));
        }

        var addrStrings = addresses.Select(a => a.ToString()).Distinct().ToList();
        var tcpSw = Stopwatch.StartNew();

        try
        {
            using var tcpClient = new TcpClient(addresses[0].AddressFamily);
            await tcpClient.ConnectAsync(addresses[0], port, linkedCts.Token);
            tcpSw.Stop();

            return new NetworkDiagnosticResult(
                true,
                true,
                addrStrings,
                dnsSw.ElapsedMilliseconds,
                tcpSw.ElapsedMilliseconds,
                L.Format("Porta TCP {0} acessível ({1} ms)", (port), (tcpSw.ElapsedMilliseconds)),
                L.Format("DNS resolvido ({0}) em {1} ms. ", (string.Join(", ", addrStrings)), (dnsSw.ElapsedMilliseconds)) +
                L.Format("Conexão TCP na porta {0} estabelecida em {1} ms.\n", (port), (tcpSw.ElapsedMilliseconds)) +
                L.Text("Nota: Isso confirma apenas que a porta de rede está acessível (handshake TCP), e NÃO significa que o usuário está autenticado no serviço."));
        }
        catch (OperationCanceledException)
        {
            tcpSw.Stop();
            var wasUserCancel = cancellationToken.IsCancellationRequested;
            return new NetworkDiagnosticResult(
                true,
                false,
                addrStrings,
                dnsSw.ElapsedMilliseconds,
                tcpSw.ElapsedMilliseconds,
                wasUserCancel ? L.Text("Teste cancelado") : L.Format("Timeout TCP na porta {0}", (port)),
                wasUserCancel
                    ? L.Text("O diagnóstico TCP foi cancelado pelo usuário.")
                    : L.Format("DNS resolvido ({0}), porém a porta TCP {1} não respondeu dentro de {2} ms (possível firewall, VPN desconectada ou serviço inativo).", (string.Join(", ", addrStrings)), (port), (timeoutMs)));
        }
        catch (Exception ex)
        {
            tcpSw.Stop();
            return new NetworkDiagnosticResult(
                true,
                false,
                addrStrings,
                dnsSw.ElapsedMilliseconds,
                tcpSw.ElapsedMilliseconds,
                L.Format("Porta TCP {0} inacessível", (port)),
                L.Format("DNS resolvido ({0}), mas a conexão TCP na porta {1} falhou: {2}", (string.Join(", ", addrStrings)), (port), (ex.Message)));
        }
    }
}
