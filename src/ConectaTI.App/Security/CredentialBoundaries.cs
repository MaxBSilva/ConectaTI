using ConectaTI.App.Models;
namespace ConectaTI.App.Security;

public interface ICredentialLookup
{
    Task<SavedCredential?> GetCredentialByIdAsync(string id);
}
public interface ICredentialSecretReader
{
    Task<string> ReadSecretAsync(SavedCredential credential);
}
// Implementations must recheck canCopy before each native write/retry.
public interface ITextClipboard
{
    Task<bool> CopyTextWithRetryAsync(string text, bool isSecret = false, int autoClearSeconds = 0, Func<bool>? canCopy = null);
}
