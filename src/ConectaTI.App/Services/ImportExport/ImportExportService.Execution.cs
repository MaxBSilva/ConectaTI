using System.Diagnostics;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;

namespace ConectaTI.App.Services.ImportExport;

public partial class ImportExportService
{
    internal void LockVaultSession() => _vaultService.Lock();

    private sealed class InlineImportProgress(Action<ImportProgress> report) : IProgress<ImportProgress> { public void Report(ImportProgress value) => report(value); }
    private readonly SemaphoreSlim _importGate = new(1, 1);
    public bool IsImportRunning => _importGate.CurrentCount == 0;
    public long? LastImportFileSize { get; private set; }
    public TimeSpan LastImportDuration { get; private set; }
    public int? LastIdentifiedCount { get; private set; }

    public async Task<ImportPreviewResult> ReadPreviewAsync(string path, IReadOnlyList<ConnectionEntry> existing,
        string? password = null, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        if (!await _importGate.WaitAsync(0, token)) throw new InvalidOperationException(L.Text("Já existe uma importação em andamento."));
        var clock = Stopwatch.StartNew();
        var startedAtUtc = DateTime.UtcNow;
        LastImportFileSize = null;
        LastIdentifiedCount = null;
        try
        {
            ImportLimits.CheckFile(path);
            LastImportFileSize = new FileInfo(path).Length;
            LastIdentifiedCount = null;
            progress?.Report(new("Validating"));
            var result = await Task.Run(() =>
            {
                var tracking = new InlineImportProgress(value => { if (value.Completed > 0) LastIdentifiedCount = value.Completed; progress?.Report(value); });
                using var scope = AutoPuttyCodec.BeginReadScope(token, tracking);
                token.ThrowIfCancellationRequested();
                return Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".csv" => ReadCsvPreview(path, existing),
                    ".json" => ParseJsonPreview(path, existing),
                    ".xml" => ParseXmlPreview(path, existing, password),
                    _ => EvaluateAndParseAutoPuttyFile(path, existing)
                };
            }, token);
            token.ThrowIfCancellationRequested();
            ImportLimits.CheckCount(result.Items.Count);
            LastIdentifiedCount = result.Items.Count;
            _logger.EnrichImportFailure(startedAtUtc, LastImportFileSize, LastIdentifiedCount, clock.Elapsed);
            return result;
        }
        catch (Exception exception)
        {
            _logger.RecordFailure("Import.Validate", exception, LastImportFileSize, LastIdentifiedCount, clock.Elapsed);
            throw;
        }
        finally { LastImportDuration = clock.Elapsed; _importGate.Release(); }
    }

    private static string Identity(ConnectionEntry entry) => string.Join('\0',
        (entry.ClientName ?? "").Trim().ToUpperInvariant(), (entry.Name ?? "").Trim().ToUpperInvariant(),
        (entry.Host ?? "").Trim().ToUpperInvariant(), entry.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private ImportPreviewResult ReadCsvPreview(string path, IReadOnlyList<ConnectionEntry> existing)
    {
        var document = ParseCsvFile(path);
        return BuildCsvPreview(document, document.SuggestedMapping, existing);
    }

    public async Task<ImportExecutionReport> CommitImportAsync(ImportPreviewResult preview,
        DuplicateImportHandling duplicateHandling, bool skipBackupCreation = false, string? existingBackupPath = null,
        CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        // Capture the confirmed selection before any asynchronous operation.
        var confirmed = preview.Items.Select(i => new
        {
            i.RowNumber, i.IsSelected, i.IsValid, i.SourceCredentialKey, i.SourceCredentialMetadata, Entry = i.Entry.Clone(), Password = i.RecoveredPlainPassword
        }).ToArray();
        var standalone=preview.StandaloneCredentials.Select(c=>new PortableCredentialRecord
        { Id=c.Id,Name=c.Name,Username=c.Username,Domain=c.Domain,Notes=c.Notes,PlainSecretForEncryptedBundleOnly=c.PlainSecretForEncryptedBundleOnly }).ToArray();
        ImportLimits.CheckCount(confirmed.Length);
        ImportLimits.CheckCount(standalone.Length);
        using var redactions = _logger.Redact(confirmed.SelectMany(i => new[]
            { i.Password, i.Entry.Name, i.Entry.Host, i.Entry.Username, i.Entry.Domain, i.Entry.Notes }).Concat(standalone.Select(c=>c.PlainSecretForEncryptedBundleOnly)));
        if (!await _importGate.WaitAsync(0, token)) throw new InvalidOperationException(L.Text("Já existe uma importação em andamento."));
        var clock = Stopwatch.StartNew();
        var gateHeld = false;
        var stage = "Validating";
        var failureRecorded = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await _repository.CredentialOperationGate.WaitAsync(token);
            gateHeld = true;
            progress?.Report(new(stage, 0, confirmed.Length));
            var existing = await _repository.GetAllConnectionsAsync(includeDeleted: true);
            var identities = existing.Where(c => !c.IsDeleted).GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
            var byId = existing.ToDictionary(c => c.Id);
            var originalIds = byId.Keys.ToHashSet(StringComparer.Ordinal);
            var storedCredentials = await _repository.GetAllCredentialsAsync();
            var validCredentials = storedCredentials.Select(c => c.Id).ToHashSet();
            var entries = new List<ConnectionEntry>();
            var credentials = new List<SavedCredential>();
            var portableCredentialMap = new Dictionary<string,SavedCredential>();
            var standaloneImported=0; var standaloneIgnored=0;
            var standaloneSavedIds=new HashSet<string>();
            var inserted = 0; var updated = 0; var ignored = 0; var invalid = confirmed.Count(i => !i.IsSelected && !i.IsValid); var duplicated = 0;
            await Task.Run(() =>
            {
                for (var record = 0; record < confirmed.Length; record++)
                {
                    var item = confirmed[record];
                    if (!item.IsSelected) continue;
                    token.ThrowIfCancellationRequested();
                    if (!item.IsValid || ConnectionValidator.ValidateConnection(item.Entry).Count != 0) { invalid++; continue; }
                    var entry = item.Entry;
                    var key = Identity(entry);
                    identities.TryGetValue(key, out var match);
                    if (match == null && byId.TryGetValue(entry.Id, out var idMatch) && !idMatch.IsDeleted) match = idMatch;
                    if (match != null && duplicateHandling == DuplicateImportHandling.Ignore) { ignored++; continue; }
                    if (match != null && duplicateHandling == DuplicateImportHandling.Update)
                    {
                        entry.Id = match.Id;
                        entry.CreatedAtUtc = match.CreatedAtUtc;
                        entry.CredentialId = match.CredentialId;
                        updated++;
                    }
                    else
                    {
                        if (match != null || byId.ContainsKey(entry.Id) || string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("D");
                        if (match != null) duplicated++;
                        if (entry.CredentialId != null && !validCredentials.Contains(entry.CredentialId)) entry.CredentialId = null;
                        inserted++;
                    }
                    if (!string.IsNullOrEmpty(item.Password))
                    {
                        if (item.SourceCredentialKey != null && portableCredentialMap.TryGetValue(item.SourceCredentialKey,out var shared))
                        {
                            entry.CredentialId=shared.Id;
                        }
                        else
                        {
                        var source=item.SourceCredentialMetadata;
                        var credential = new SavedCredential { Name = source?.Name ?? entry.Name, Username = source?.Username ?? entry.Username,
                            Domain = source?.Domain ?? entry.Domain, Notes=source?.Notes ?? "" };
                        try
                        {
                            credential.ProtectedSecretBase64 = _vaultService.ProtectSecret(credential.Id, credential.EffectiveUserDisplay, item.Password, false);
                        }
                        catch (Exception exception)
                        {
                            _logger.RecordFailure("Import.Credentials.ProtectSecret", exception, LastImportFileSize,
                                confirmed.Length, clock.Elapsed, record + 1, item.RowNumber);
                            failureRecorded = true;
                            throw;
                        }
                        credentials.Add(credential);
                        entry.CredentialId = credential.Id;
                        if(item.SourceCredentialKey!=null) portableCredentialMap.Add(item.SourceCredentialKey,credential);
                        }
                    }
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    entries.Add(entry);
                    identities[key] = entry;
                    byId[entry.Id] = entry;
                }
            }, token);
            // Multiple file rows may target the same ID. Persist its final confirmed value once.
            var uniqueEntries = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();
            ignored += entries.Count - uniqueEntries.Count;
            entries = uniqueEntries;
            var referenced = entries.Select(e => e.CredentialId).ToHashSet();
            credentials.RemoveAll(c => !referenced.Contains(c.Id));
            await Task.Run(() =>
            {
                foreach(var item in standalone)
                {
                    token.ThrowIfCancellationRequested();
                    var match=storedCredentials.FirstOrDefault(c=>c.Id==item.Id) ?? storedCredentials.FirstOrDefault(c=>
                        string.Equals(c.Name.Trim(),item.Name.Trim(),StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Username.Trim(),item.Username.Trim(),StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Domain.Trim(),item.Domain.Trim(),StringComparison.OrdinalIgnoreCase));
                    if(match!=null && duplicateHandling==DuplicateImportHandling.Ignore) {standaloneIgnored++;continue;}
                    var update=match!=null && duplicateHandling==DuplicateImportHandling.Update;
                    var credential=new SavedCredential {Id=update ? match!.Id : Guid.NewGuid().ToString("D"),
                        Name=item.Name,Username=item.Username,Domain=item.Domain,Notes=item.Notes,
                        CreatedAtUtc=update ? match!.CreatedAtUtc : DateTime.UtcNow,
                        LegacyProtectedSecretBase64=update ? match!.LegacyProtectedSecretBase64 : "",
                        LegacySecretStorage=update ? match!.LegacySecretStorage : null};
                    if(!string.IsNullOrEmpty(item.PlainSecretForEncryptedBundleOnly))
                        credential.ProtectedSecretBase64=_vaultService.ProtectSecret(credential.Id,credential.EffectiveUserDisplay,item.PlainSecretForEncryptedBundleOnly);
                    else if(update)
                    {
                        credential.ProtectedSecretBase64=match!.ProtectedSecretBase64;
                        credential.SecretStorage=match.SecretStorage;
                    }
                    credentials.Add(credential);if(standaloneSavedIds.Add(credential.Id))standaloneImported++;
                }
            },token);
            credentials=credentials.GroupBy(c=>c.Id).Select(g=>g.Last()).ToList();
            inserted = entries.Count(e => !originalIds.Contains(e.Id));
            updated = entries.Count - inserted;
            token.ThrowIfCancellationRequested();
            stage = "Backup"; progress?.Report(new(stage));
            var backup = skipBackupCreation && existingBackupPath != null ? existingBackupPath : await _repository.CreateBackupAsync(null, "pre-import");
            var persistence = new ConnectionPersistenceService(_repository, _vaultService, _logger);
            try
            {
                stage = "Importing";
                await _repository.CommitImportBatchAsync(entries, credentials, token, progress);
            }
            catch
            {
                await persistence.CleanupWithGateHeldAsync();
                throw;
            }
            // From this point the transaction was committed. Do not check cancellation again.
            var clean = await persistence.CleanupWithGateHeldAsync();
            progress?.Report(new("Completed", entries.Count, entries.Count));
            return new(inserted, updated, ignored, invalid, backup)
            {
                StandaloneCredentialsImported=standaloneImported, StandaloneCredentialsIgnored=standaloneIgnored,
                CredentialsImportedCount = credentials.Count, FoundCount = confirmed.Length, DuplicatedCount = duplicated,
                UnselectedCount = confirmed.Count(i => !i.IsSelected && i.IsValid), CleanupPending = !clean
            };
        }
        catch (Exception exception)
        {
            if (!failureRecorded) _logger.RecordFailure("Import." + stage, exception, LastImportFileSize, confirmed.Length, clock.Elapsed);
            throw;
        }
        finally
        {
            LastImportDuration = clock.Elapsed;
            if (gateHeld) _repository.CredentialOperationGate.Release();
            _importGate.Release();
        }
    }
}
