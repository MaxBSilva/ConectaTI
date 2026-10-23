namespace ConectaTI.App.Models;

public enum PasswordEditIntent { Keep, Replace, Remove }

// Session-only state. Neither drafts nor their plaintext passwords are serialized.
internal sealed class ConnectionEditSession(ConnectionEntry entry, bool isNew = false)
{
    public ConnectionEntry Entry { get; set; } = entry.Clone();
    public ConnectionEntry Baseline { get; set; } = entry.Clone();
    public bool IsNew { get; set; } = isNew;
    public string Password { get; set; } = string.Empty;
    public string? LoadedPassword { get; set; }
    public PasswordEditIntent PasswordIntent { get; set; }
    public string CredentialName { get; set; } = string.Empty;
    public string HostText { get; set; } = Helpers.HostPortParser.FormatDisplay(entry.Host, entry.Port, entry.Protocol);
    public string UserText { get; set; } = Helpers.UserIdentityFormatter.FormatDisplay(entry.Username, entry.Domain);
    public long Revision { get; set; }
    public long PasswordRevision { get; set; }
    public bool IsDirty => !Entry.HasSameEditableValues(Baseline) || PasswordIntent != PasswordEditIntent.Keep;
}
