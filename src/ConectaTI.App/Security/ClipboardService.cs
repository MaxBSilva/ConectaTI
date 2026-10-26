using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ConectaTI.App.Services;

namespace ConectaTI.App.Security;

/// <summary>
/// Serviço seguro para operações de área de transferência:
/// - Trata temporariamente área de transferência ocupada por outro processo (COMException / CLIPBRD_E_CANT_OPEN).
/// - Ao copiar senha, agenda limpeza automática após um intervalo, somente se o conteúdo atual ainda for aquele copiado pelo aplicativo.
/// </summary>
public class ClipboardService
{
    private readonly AppLogger _logger;
    private readonly Func<string, bool> _setClipboardAction;
    private readonly Func<string?> _getClipboardAction;
    private readonly Action _clearClipboardAction;

    private byte[]? _lastCopiedSecretHash;
    private CancellationTokenSource? _autoClearCts;

    public ClipboardService(
        AppLogger logger,
        Func<string, bool>? customSetClipboard = null,
        Func<string?>? customGetClipboard = null,
        Action? customClearClipboard = null)
    {
        _logger = logger;
        _setClipboardAction = customSetClipboard ?? DefaultSetClipboardText;
        _getClipboardAction = customGetClipboard ?? DefaultGetClipboardText;
        _clearClipboardAction = customClearClipboard ?? DefaultClearClipboard;
    }

    public async Task<bool> CopyTextWithRetryAsync(string text, bool isSecret = false, int autoClearSeconds = 0,
        Func<bool>? canCopy = null)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var copied = false;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            if (canCopy?.Invoke() == false) return false;
            try
            {
                if (_setClipboardAction(text))
                {
                    copied = true;
                    break;
                }
            }
            catch (COMException)
            {
                // Área de transferência temporariamente bloqueada por outro processo
            }
            catch (Exception ex)
            {
                _logger.Warn("Clipboard", $"Tentativa {attempt} falhou ao acessar área de transferência: {ex.GetType().Name}");
            }

            await Task.Delay(45 * attempt);
        }

        if (!copied)
        {
            _logger.Warn("Clipboard", "Não foi possível copiar para a área de transferência após várias tentativas.");
            return false;
        }

        _autoClearCts?.Cancel();
        _autoClearCts?.Dispose();
        _autoClearCts = null;

        if (isSecret)
        {
            _lastCopiedSecretHash = ComputeSha256(text);
            if (autoClearSeconds > 0)
            {
                var cts = new CancellationTokenSource();
                _autoClearCts = cts;
                _ = ScheduleAutoClearAsync(TimeSpan.FromSeconds(autoClearSeconds), _lastCopiedSecretHash, cts.Token);
            }
        }
        else
        {
            _lastCopiedSecretHash = null;
        }

        return true;
    }

    public bool ClearIfMatchesHash(byte[] expectedHash)
    {
        try
        {
            var currentText = _getClipboardAction();
            if (string.IsNullOrEmpty(currentText))
                return false;

            var currentHash = ComputeSha256(currentText);
            if (CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
            {
                _clearClipboardAction();
                _logger.Info("Clipboard", "Senha temporária removida automaticamente da área de transferência.");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Clipboard", $"Não foi possível limpar a área de transferência automaticamente: {ex.GetType().Name}");
        }

        return false;
    }

    private async Task ScheduleAutoClearAsync(TimeSpan delay, byte[] expectedHash, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            if (token.IsCancellationRequested)
                return;

            if (Application.Current?.Dispatcher != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => ClearIfMatchesHash(expectedHash));
            }
            else
            {
                ClearIfMatchesHash(expectedHash);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado por nova cópia
        }
    }

    private static byte[] ComputeSha256(string input)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(input));
    }

    private static bool DefaultSetClipboardText(string text)
    {
        Clipboard.SetDataObject(text, false);
        return true;
    }

    private static string? DefaultGetClipboardText()
    {
        return Clipboard.ContainsText() ? Clipboard.GetText() : null;
    }

    private static void DefaultClearClipboard()
    {
        Clipboard.Clear();
    }
}
