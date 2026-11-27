using ConectaTI.App.Models;
using ConectaTI.App.Services;
namespace ConectaTI.App.Security;

internal enum CredentialReadStatus { Available, Missing, Failed, Obsolete, Cancelled }

// Deliberately not a record: generated ToString must never print a secret.
internal sealed class CredentialReadResult(CredentialReadStatus status, SavedCredential? credential = null, string? secret = null, Exception? error = null)
{
    public CredentialReadStatus Status { get; } = status;
    public SavedCredential? Credential { get; } = credential;
    public string? Secret { get; } = secret;
    public Exception? Error { get; } = error;
}

// Coordinates credential reads/copies without WPF, DPAPI, SQLite or a system clipboard dependency.
internal sealed class ConnectionCredentialService(ICredentialLookup lookup, ICredentialSecretReader secrets, ITextClipboard clipboard)
{
    public async Task<CredentialReadResult> ReadAsync(string? id, Func<bool> current, CancellationToken cancellation = default)
    {
        CredentialReadResult? Abandoned() => cancellation.IsCancellationRequested ? new(CredentialReadStatus.Cancelled)
            : !current() ? new(CredentialReadStatus.Obsolete) : null;
        if (Abandoned() is { } abandoned) return abandoned;
        if (string.IsNullOrWhiteSpace(id)) return new(CredentialReadStatus.Missing);
        SavedCredential? credential = null;
        try
        {
            credential = await lookup.GetCredentialByIdAsync(id);
            if (Abandoned() is { } afterLookup) return afterLookup;
            if (credential == null) return new(CredentialReadStatus.Missing);
            var secret = await secrets.ReadSecretAsync(credential);
            if (Abandoned() is { } afterRead) return afterRead;
            return new(CredentialReadStatus.Available, credential, secret);
        }
        catch (Exception error)
        {
            if (Abandoned() is { } afterFailure) return afterFailure;
            return new(CredentialReadStatus.Failed, credential, error: error);
        }
    }

    public Task<bool> CopyPasswordAsync(string secret, Func<bool> current, int clearSeconds)
        => !current() ? Task.FromResult(false) : clipboard.CopyTextWithRetryAsync(secret, isSecret: true, autoClearSeconds: clearSeconds, canCopy: current);

    public Task<bool> CopyConnectionAsync(ConnectionEntry snapshot, string host, string user, string? secret, Func<bool> current, int clearSeconds, string? vpnSecret = null)
        => !current() ? Task.FromResult(false) : clipboard.CopyTextWithRetryAsync(ConnectionCopyFormatter.Format(snapshot, host, user, secret, vpnSecret),
            isSecret: true, autoClearSeconds: clearSeconds, canCopy: current);
}
