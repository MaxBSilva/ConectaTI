namespace ConectaTI.App.Models;

public enum PasswordEditIntent { Keep, Replace, Remove }

// Session-only state. Neither drafts nor their plaintext passwords are serialized.
internal sealed class ConnectionEditSession(ConnectionEntry entry, bool isNew = false)
{
    public bool CredentialRecoveryFailed { get; set; }
    public string? CredentialRecoveryMessage { get; set; }

    public ConnectionEntry Entry { get; set; } = entry.Clone();
    public ConnectionEntry Baseline { get; set; } = entry.Clone();
    public bool IsNew { get; set; } = isNew;
    public string Password { get; set; } = string.Empty;
    public string? LoadedPassword { get; set; }
    public PasswordEditIntent PasswordIntent { get; set; }
    public string CredentialName { get; set; } = string.Empty;
    public string HostText { get; set; } = Helpers.HostPortParser.FormatDisplay(entry.Host, entry.Port, entry.Protocol);
    public string UserText { get; set; } = Helpers.UserIdentityFormatter.FormatDisplay(entry.Username, entry.Domain);
    public long Revision { get; private set; }
    public long PasswordRevision { get; private set; }
    public bool IsDirty => !Entry.HasSameEditableValues(Baseline) || PasswordIntent != PasswordEditIntent.Keep;

    public void RecordFieldEdit() => Revision++;

    public void CaptureForm(string host, string user, string password, string credentialName)
    { HostText = host; UserText = user; Password = password; CredentialName = credentialName; }

    public void UpdatePassword(string value)
    {
        Password = value; PasswordRevision++; Revision++;
        PasswordIntent = value.Length > 0
            ? value == LoadedPassword ? PasswordEditIntent.Keep : PasswordEditIntent.Replace
            : LoadedPassword != null && !string.IsNullOrEmpty(Entry.CredentialId)
                ? PasswordEditIntent.Remove : PasswordEditIntent.Keep;
    }

    public void SetPasswordRemovalIntent(bool remove, bool hasInlinePassword)
    {
        PasswordIntent = remove ? PasswordEditIntent.Remove : hasInlinePassword ? PasswordEditIntent.Replace : PasswordEditIntent.Keep;
        Revision++; PasswordRevision++;
    }

    public void RequestPasswordRemoval()
    {
        Password = string.Empty;
        PasswordIntent = string.IsNullOrEmpty(Entry.CredentialId) ? PasswordEditIntent.Keep : PasswordEditIntent.Remove;
        Revision++; PasswordRevision++;
    }

    public ConnectionSaveSubmission CaptureSave() => new(this);

    public void AcceptSave(ConnectionSaveSubmission submitted, ConnectionEntry persisted)
    {
        if (!ReferenceEquals(submitted.Session, this) || persisted.Id != Entry.Id)
            throw new ArgumentException("Save does not belong to this editing session.", nameof(submitted));
        Baseline = persisted.Clone(); IsNew = false;
        var unchanged = Revision == submitted.Revision;
        if (unchanged) Entry = persisted.Clone();
        else { Entry.CredentialId = persisted.CredentialId; Entry.UpdatedAtUtc = persisted.UpdatedAtUtc; }
        if (PasswordRevision == submitted.PasswordRevision)
        {
            if (submitted.Intent is PasswordEditIntent.Replace or PasswordEditIntent.Remove)
            { CredentialRecoveryFailed = false; CredentialRecoveryMessage = null; }
            Password = string.Empty; LoadedPassword = null; PasswordIntent = PasswordEditIntent.Keep;
        }
        else
        {
            if (submitted.Intent == PasswordEditIntent.Replace) LoadedPassword = submitted.Password;
            else if (submitted.Intent == PasswordEditIntent.Remove) LoadedPassword = null;
            PasswordIntent = Password.Length == 0
                ? persisted.CredentialId == null ? PasswordEditIntent.Keep : PasswordEditIntent.Remove
                : Password == LoadedPassword ? PasswordEditIntent.Keep : PasswordEditIntent.Replace;
        }
        if (unchanged)
        {
            HostText = Helpers.HostPortParser.FormatDisplay(persisted.Host, persisted.Port, persisted.Protocol);
            UserText = Helpers.UserIdentityFormatter.FormatDisplay(persisted.Username, persisted.Domain);
        }
    }
}

// Captured identity/revisions and an independent snapshot; ToString never renders plaintext.
internal sealed class ConnectionSaveSubmission(ConnectionEditSession session)
{
    public ConnectionEditSession Session { get; } = session;
    public ConnectionEntry Entry { get; } = session.Entry.Clone();
    public long Revision { get; } = session.Revision;
    public long PasswordRevision { get; } = session.PasswordRevision;
    public string Password { get; } = session.Password;
    public PasswordEditIntent Intent { get; } = session.PasswordIntent;
    public string CredentialName { get; } = session.CredentialName;
}
