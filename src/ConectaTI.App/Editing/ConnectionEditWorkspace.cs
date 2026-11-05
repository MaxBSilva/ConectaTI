using System.Collections.ObjectModel;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
namespace ConectaTI.App.Editing;

// Owns session lifetime, independent of WPF controls, persistence and selection events.
internal sealed class ConnectionEditWorkspace
{
    private readonly Dictionary<string, ConnectionEditSession> _sessions = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ConnectionEditSession> Sessions => _sessions;
    public ConnectionEditSession? Active { get; private set; }
    public long Generation { get; private set; }
    public ObservableCollection<ConnectionEntry> NewDrafts { get; } = new();

    public ConnectionEditSession GetOrCreate(ConnectionEntry persisted, bool saveInProgress)
    {
        if (!_sessions.TryGetValue(persisted.Id, out var session))
            _sessions[persisted.Id] = session = new ConnectionEditSession(persisted);
        else if (!session.IsDirty && !saveInProgress)
        {
            session.Entry = persisted.Clone(); session.Baseline = persisted.Clone();
            session.HostText = HostPortParser.FormatDisplay(persisted.Host, persisted.Port, persisted.Protocol);
            session.UserText = UserIdentityFormatter.FormatDisplay(persisted.Username, persisted.Domain);
        }
        return session;
    }

    public void AddDraft(ConnectionEditSession session)
    {
        _sessions[session.Entry.Id] = session;
        NewDrafts.Add(session.Entry);
    }
    public void Activate(ConnectionEditSession? session) { Generation++; Active = session; }
    public void Forget(string id) => _sessions.Remove(id);
    public void Clear()
    {
        Activate(null); _sessions.Clear(); NewDrafts.Clear();
    }
}
