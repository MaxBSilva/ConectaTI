using ConectaTI.App.Editing;
using ConectaTI.App.Models;
using ConectaTI.App.Services.Connections;

namespace ConectaTI.Tests;

public sealed class RefactorEditingAndListTests
{
    [Fact]
    public void WorkspaceKeepsDraftByStableIdAndInvalidatesPreviousActivation()
    {
        var workspace = new ConnectionEditWorkspace();
        var persisted = new ConnectionEntry { Name = "Alpha", Host = "a.example.test" };
        var session = workspace.GetOrCreate(persisted, false);
        workspace.Activate(session);
        var generation = workspace.Generation;
        session.Entry.Notes = "unsaved Alpha"; session.RecordFieldEdit();
        session.UpdatePassword("SYNTHETIC_ALPHA");
        var beta = workspace.GetOrCreate(new() { Name = "Beta" }, false);
        workspace.Activate(beta);
        Assert.Empty(beta.Password);
        Assert.Same(session, workspace.GetOrCreate(persisted.Clone(), false));
        workspace.Activate(session);
        Assert.Equal("unsaved Alpha", workspace.Active!.Entry.Notes);
        Assert.Equal("SYNTHETIC_ALPHA", workspace.Active.Password);
        Assert.True(workspace.Generation > generation);
        Assert.Empty(persisted.Notes);
        workspace.Clear();
        Assert.Null(workspace.Active); Assert.Empty(workspace.Sessions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SaveCompletionPreservesLaterFieldsAndPasswordIntent(bool eraseLater)
    {
        var session = new ConnectionEditSession(new() { Name = "Alpha", Host = "a.example.test" });
        session.UpdatePassword("SYNTHETIC_SUBMITTED");
        var submitted = session.CaptureSave();
        var persisted = submitted.Entry.Clone(); persisted.CredentialId = "new-dedicated-credential";
        session.Entry.Notes = "later edit"; session.RecordFieldEdit();
        session.UpdatePassword(eraseLater ? "" : "SYNTHETIC_LATER");
        session.AcceptSave(submitted, persisted);
        Assert.Equal("later edit", session.Entry.Notes);
        Assert.Empty(session.Baseline.Notes);
        Assert.Equal(persisted.CredentialId, session.Entry.CredentialId);
        Assert.Equal(eraseLater ? PasswordEditIntent.Remove : PasswordEditIntent.Replace, session.PasswordIntent);
        Assert.True(session.IsDirty);
        Assert.DoesNotContain("SYNTHETIC_SUBMITTED", submitted.ToString());
        var other = new ConnectionEditSession(new() { Name = "Beta" });
        Assert.Throws<ArgumentException>(() => other.AcceptSave(submitted, persisted));
    }

    [Theory]
    [InlineData("identity")] [InlineData("id")] [InlineData("credential")]
    [InlineData("generation")] [InlineData("revision")] [InlineData("password")] [InlineData("closed")]
    public void AsyncGuardRejectsEveryObsoleteEditorDimension(string changed)
    {
        var original = new EditorOperationState(new object(), "A", "credential-A", 1, 2, 3);
        var state = original;
        var guard = new EditorOperationGuard(() => state);
        Assert.True(guard.IsCurrent());
        state = changed switch
        {
            "identity" => original with { Identity = new object() },
            "id" => original with { EntryId = "B" },
            "credential" => original with { CredentialId = "credential-B" },
            "generation" => original with { Generation = 4 },
            "revision" => original with { Revision = 4 },
            "password" => original with { PasswordRevision = 4 },
            _ => original with { Identity = null }
        };
        Assert.False(guard.IsCurrent());
    }

    [Fact]
    public void QueryUsesDisplayedOrderAndKeepsManualInactivityIndependentOfTrash()
    {
        var a = new ConnectionEntry { Name = "Avícola Z", ClientName = "Cliente", GroupName = "ERP", Host = "z.example.test", IsActive = false };
        var b = new ConnectionEntry { Name = "Avícola A", ClientName = "Cliente", GroupName = "ERP", Host = "a.example.test", IsFavorite = true };
        var trash = new ConnectionEntry { Name = "Avícola Lixeira", ClientName = "Cliente", GroupName = "ERP", IsDeleted = true };
        var criteria = new ConnectionListCriteria(ConnectionViewMode.All, ConnectionSortOption.NameAsc,
            "avicola ERP", Scope: new("Cliente", "ERP", true));
        var displayed = ConnectionListQuery.Apply([a, trash, b], criteria);
        Assert.Equal(new[] { b.Id, a.Id }, displayed.Select(c => c.Id));
        Assert.Same(a, ConnectionListQuery.SelectById(displayed, a.Id));
        Assert.Same(b, ConnectionListQuery.SelectById(displayed, "removed"));
        Assert.Same(a, displayed[ConnectionListQuery.MoveIndex(displayed.Count, 0, 1)]);
        Assert.Single(ConnectionListQuery.Apply([a, trash, b], criteria with { ViewMode = ConnectionViewMode.Favorites }));
        Assert.Same(trash, Assert.Single(ConnectionListQuery.Apply([a, trash, b], criteria with { ViewMode = ConnectionViewMode.Trash })));
    }

    [Theory]
    [InlineData(0, -1, 1, -1)] [InlineData(3, -1, 1, 0)]
    [InlineData(3, -1, -1, 2)] [InlineData(3, 2, 1, 2)] [InlineData(3, 0, -1, 0)]
    public void DisplayedNavigationDoesNotWrap(int count, int current, int direction, int expected)
        => Assert.Equal(expected, ConnectionListQuery.MoveIndex(count, current, direction));

    [Fact]
    public async Task RevealReplacementAndCancellationCannotExpireANewerDisplay()
    {
        var lease = new PasswordRevealLease();
        var first = lease.BeginRequest(() => true);
        var second = lease.BeginRequest(() => true);
        Assert.False(first()); Assert.True(second());
        var oldExpired = false;
        var old = lease.ScheduleAutoHideAsync(TimeSpan.FromSeconds(5), _ => { oldExpired = true; return Task.CompletedTask; });
        var newerExpired = false;
        await lease.ScheduleAutoHideAsync(TimeSpan.Zero, _ => { newerExpired = true; return Task.CompletedTask; });
        await old;
        Assert.False(oldExpired); Assert.True(newerExpired);
        lease.Cancel(); Assert.False(second());
    }
}
