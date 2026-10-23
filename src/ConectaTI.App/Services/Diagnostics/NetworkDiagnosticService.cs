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
                "Host inválido",
                "O hostname ou endereço IP informado não possui um formato DNS, IPv4 ou IPv6 válido.");
        }

        if (!ConnectionValidator.IsValidPort(port))
        {
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                0,
                0,
                "Porta inválida",
                "A porta deve estar entre 1 e 65535.");
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
                wasUserCancel ? "Teste cancelado" : "Timeout na resolução DNS",
                wasUserCancel
                    ? "O diagnóstico de rede foi cancelado pelo usuário."
                    : $"A resolução DNS para '{cleanHost}' excedeu o tempo limite de {timeoutMs} ms.");
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
                "Falha na resolução DNS",
                $"Não foi possível resolver o endereço '{cleanHost}': {ex.Message}");
        }

        if (addresses.Length == 0)
        {
            return new NetworkDiagnosticResult(
                false,
                false,
                Array.Empty<string>(),
                dnsSw.ElapsedMilliseconds,
                0,
                "DNS sem registros",
                $"O host '{cleanHost}' não retornou nenhum endereço IPv4 ou IPv6.");
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
                $"Porta TCP {port} acessível ({tcpSw.ElapsedMilliseconds} ms)",
                $"DNS resolvido ({string.Join(", ", addrStrings)}) em {dnsSw.ElapsedMilliseconds} ms. " +
                $"Conexão TCP na porta {port} estabelecida em {tcpSw.ElapsedMilliseconds} ms.\n" +
                "Nota: Isso confirma apenas que a porta de rede está acessível (handshake TCP), e NÃO significa que o usuário está autenticado no serviço.");
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
                wasUserCancel ? "Teste cancelado" : $"Timeout TCP na porta {port}",
                wasUserCancel
                    ? "O diagnóstico TCP foi cancelado pelo usuário."
                    : $"DNS resolvido ({string.Join(", ", addrStrings)}), porém a porta TCP {port} não respondeu dentro de {timeoutMs} ms (possível firewall, VPN desconectada ou serviço inativo).");
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
                $"Porta TCP {port} inacessível",
                $"DNS resolvido ({string.Join(", ", addrStrings)}), mas a conexão TCP na porta {port} falhou: {ex.Message}");
        }
    }
}
