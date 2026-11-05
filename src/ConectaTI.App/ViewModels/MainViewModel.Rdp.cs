using ConectaTI.App.Models;
using ConectaTI.App.Services;
namespace ConectaTI.App.ViewModels;
public partial class MainViewModel
{
    public Task<string> BuildRdpPreviewAsync(ConnectionEntry snapshot) => _launcherService.BuildSafeRdpPreviewAsync(snapshot);
    public void ImportRdpDraft(RdpImportResult result)
    {
        CaptureSession();
        var session = new ConnectionEditSession(result.Entry, isNew: true);
        // Imported values are unsaved changes even before the user edits a field.
        session.Baseline = new ConnectionEntry { Id = session.Entry.Id };
        OpenNewDraft(session);
        StatusMessage = string.Join(" ", result.Warnings);
    }
}


