namespace ConectaTI.App.Editing;

// An async editor operation belongs to one object identity and one captured revision.
internal readonly record struct EditorOperationState(
    object? Identity, string? EntryId, string? CredentialId, long Generation, long Revision, long PasswordRevision);

internal sealed class EditorOperationGuard(Func<EditorOperationState> readState)
{
    private readonly EditorOperationState _captured = readState();
    public bool IsCurrent()
    {
        var current = readState();
        return _captured.Identity != null && ReferenceEquals(_captured.Identity, current.Identity)
            && _captured.EntryId == current.EntryId && _captured.CredentialId == current.CredentialId
            && _captured.Generation == current.Generation && _captured.Revision == current.Revision
            && _captured.PasswordRevision == current.PasswordRevision;
    }
}
