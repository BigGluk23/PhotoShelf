using System.IO;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Media;

namespace PhotoShelf.Desktop;

public sealed record CatalogExternalRename(string Source, SavedMediaItem Destination);
public sealed record LibraryCatalogUpdate(IReadOnlyList<SavedMediaItem> Items,
    IReadOnlyList<CatalogExternalRename> Renames, IReadOnlyList<LibraryRootState> Roots, bool Completed,
    bool CatalogChanged = false);

/// <summary>Worker-only reconciliation. Observes originals without modifying them; writes only the SQLite catalog.</summary>
public sealed class LibraryCatalogSynchronizer(
    SqliteDesktopCatalogStore store,
    Func<LibraryCatalogUpdate, Task> publish,
    IFileSystemObservationProbe? observationProbe = null, BackgroundWorkController? workController = null)
{
    private readonly IFileSystemObservationProbe _probe = observationProbe ?? new FileSystemObservationProbe();

    public async Task ProcessAsync(LibraryMonitorBatch batch, FolderInclusionRules inclusion,
        string? browsedFolder, bool browseRecursively, bool includeSystem, CancellationToken token,
        IReadOnlyList<string>? libraryRoots = null)
    {
        if (batch.BrowseTarget is { } browse)
        {
            // Foreground navigation is an immutable, exact request. It can read an
            // unchecked folder without broadening library rules or inheriting a
            // recursive ancestor's scope, even if the user has since selected elsewhere.
            browsedFolder = browse.Path;
            browseRecursively = browse.IncludeSubdirectories;
            libraryRoots = [];
            inclusion = new FolderInclusionRules([]);
        }
        libraryRoots ??= batch.ReconcileRoots.Concat((batch.DirectoryChanges ?? []).Select(change => change.OwnerRoot))
            .Where(root => !root.Equals(browsedFolder, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool InLibrary(string path) => libraryRoots.Any(root => IsUnder(path, root)) && inclusion.IsIncluded(path);
        bool Observe(string path) => !PhotoScanner.IsIgnoredPath(path, includeSystem) &&
            (InLibrary(path) || IsBrowsed(path));
        bool IsBrowsed(string path) => browsedFolder is not null &&
            (browseRecursively ? IsUnder(path, browsedFolder) :
                string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), browsedFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

        using var activity = workController?.Begin(BackgroundTaskKind.Scan);
        var pacer = batch.BrowseTarget is null ? workController?.CreatePacer() : null;
        async Task CheckpointAsync(CancellationToken ct)
        {
            // Exact foreground browse keeps its priority; background traversal yields only
            // between completed reads and never abandons a live directory enumerator.
            if (pacer is not null) await pacer.CheckpointAsync(ct, activity);
            activity?.SetPhase(BackgroundTaskPhase.Reading);
        }
        async Task ObserveBatchAsync(IReadOnlyList<string> paths, bool force)
        {
            await CheckpointAsync(token);
            await ObservePathsCoreAsync(paths, force, token, MarkCommitted);
            activity?.Progress(paths.Count);
        }
        var catalogChanged = false;
        void MarkCommitted() => catalogChanged = true;
        try
        {
            await publish(new([], [], batch.RootStates, false));
            foreach (var paths in batch.ChangedPaths.Where(Observe).Where(PhotoItem.IsSupported).Chunk(128))
                await ObserveBatchAsync(paths, true);

            // A directory notification describes its own old/new subtree. It must not
            // restart enumeration of the whole watched drive. The catalog pass below
            // also covers a deleted subtree without deleting any catalog records.
            var targets = batch.BrowseTarget is { } requestedBrowse ? [requestedBrowse.Path] :
                batch.ReconcileRoots.Concat((batch.DirectoryChanges ?? []).Select(change => change.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in targets)
            {
                token.ThrowIfCancellationRequested();
                var rootResult = _probe.ProbeRoot(root);
                var recursive = libraryRoots.Any(parent => IsUnder(root, parent)) && inclusion.MayContainIncluded(root) || browseRecursively && browsedFolder is not null && IsUnder(root, browsedFolder);
                if (rootResult.Availability == FileAvailability.Available)
                {
                    // A browsed, unchecked subtree remains navigable without becoming part of the library.
                    var scanRules = inclusion.Snapshot();
                    if (browsedFolder is not null && IsUnder(root, browsedFolder)) scanRules.SetIncluded(root, true);
                    await PhotoScanner.ScanAsync([root], scan => ObserveBatchAsync(scan.Paths.Where(Observe).ToArray(),
                        batch.RevalidateContentRoots?.Contains(root, StringComparer.OrdinalIgnoreCase) == true),
                        includeSystem, token, recursive: recursive, inclusion: scanRules, checkpoint: CheckpointAsync);
                }

                // A second bounded pass covers missing/inaccessible files which enumeration cannot report.
                // Never interpret a failed directory enumeration as deletion of catalog rows.
                // A direct-folder browse must not enumerate nested library roots through this pass.
                string? after = null;
                while (true)
                {
                    var page = await store.QuerySubtreePageAsync(root, after, 128, token, includeSubdirectories: recursive);
                    if (page.Count == 0) break;
                    after = page[^1].Path;
                    var candidates = page.Where(item => Observe(item.Path)).ToArray();
                    if (rootResult.Availability != FileAvailability.Available)
                    {
                        var updates = candidates.Where(item => item.Availability != rootResult.Availability ||
                            item.AvailabilityErrorCode != rootResult.ErrorCode).Select(item =>
                            (new FileObservation(Unavailable(item.Path, rootResult)), (SavedMediaItem?)item)).ToArray();
                        await ApplyAsync(updates, [], token, MarkCommitted);
                    }
                    else await ObserveBatchAsync(candidates.Select(item => item.Path).ToArray(), false);
                }
            }
            token.ThrowIfCancellationRequested();
            activity?.Finish(BackgroundTaskPhase.Completed);
        }
        catch (OperationCanceledException) { activity?.Finish(BackgroundTaskPhase.Cancelled); throw; }
        finally
        {
            // Committed work still needs its metadata/count/publication boundary if this
            // generation was cancelled midway. This notification performs no file reads.
            await publish(new([], [], [], true, catalogChanged));
        }
    }

    public Task<bool> ObservePathsAsync(IReadOnlyList<string> paths, bool forceContent, CancellationToken token) =>
        ObservePathsCoreAsync(paths, forceContent, token, null);

    private async Task<bool> ObservePathsCoreAsync(IReadOnlyList<string> paths, bool forceContent, CancellationToken token,
        Action? onCommitted)
    {
        var catalogChanged = false;
        void MarkCommitted() { catalogChanged = true; onCommitted?.Invoke(); }
        foreach (var part in paths.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(128))
        {
            var known = await store.GetItemsByPathsAsync(part, token);
            var changes = new List<(FileObservation, SavedMediaItem?)>(part.Length);
            var renames = new List<CatalogExternalRename>();
            try
            {
                foreach (var path in part)
                {
                    token.ThrowIfCancellationRequested();
                    var result = _probe.ProbeFile(path);
                    if (result.IsDirectory) continue;
                    var current = result.Availability == FileAvailability.Available ? Available(result) : Unavailable(path, result);
                    known.TryGetValue(path, out var previous);
                    var revalidate = forceContent || previous?.Availability is FileAvailability.AccessDenied or FileAvailability.RootOffline ||
                        previous is { Availability: FileAvailability.NeedsVerification, AvailabilityErrorCode: not null };
                    if (previous is null && current.Availability != FileAvailability.Available) continue;
                    // .ts/.mts are also source-code extensions. Probe only new admissions,
                    // on this worker path; never remove/reclassify existing catalog entries.
                    if (previous is null && MediaAdmissionProbe.Probe(path, token) == MediaAdmissionKind.NonMediaText) continue;
                    if (previous is null && current.FileIdentity is { Length: > 0 } identity)
                    {
                        var matches = await store.FindByFileIdentityAsync(identity, token: token);
                        if (matches.Count == 1 && !matches[0].Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                            _probe.ProbeFile(matches[0].Path).Availability == FileAvailability.Missing)
                        {
                            var confirm = _probe.ProbeFile(path);
                            if (confirm.Availability == FileAvailability.Available && confirm.FileIdentity == current.FileIdentity &&
                                confirm.Length == current.SizeBytes && confirm.LastWriteTimeUtc == current.FileModifiedAt?.ToUniversalTime() &&
                                await store.TryReconcileExternalRenameAsync(matches[0], current, token))
                            {
                                // Record the committed identity change before any further cancellable work.
                                renames.Add(new(matches[0].Path, current));
                                MarkCommitted();
                                if (forceContent && await store.GetItemAsync(path, CancellationToken.None) is { } moved)
                                    await store.ApplyObservationAsync(new(current, true), moved, CancellationToken.None);
                                continue;
                            }
                        }
                    }
                    if (previous is not null && !revalidate && Equivalent(previous, current)) continue;
                    if (previous is not null && current.Availability != FileAvailability.Available &&
                        previous.Availability == current.Availability && previous.AvailabilityErrorCode == current.AvailabilityErrorCode) continue;
                    changes.Add((new(current, revalidate && current.Availability == FileAvailability.Available), previous));
                }
                await ApplyAsync(changes, [], token, MarkCommitted);
            }
            finally
            {
                // Cancellation after commit must not leave selection/viewers on the old path.
                await ApplyAsync([], renames, CancellationToken.None, MarkCommitted);
            }
        }
        return catalogChanged;
    }

    private async Task<bool> ApplyAsync(IReadOnlyList<(FileObservation Observation, SavedMediaItem? Expected)> changes,
        IReadOnlyList<CatalogExternalRename> renames, CancellationToken token, Action onCommitted)
    {
        if (changes.Count == 0 && renames.Count == 0) return false;
        IReadOnlyList<bool> accepted = changes.Count == 0 ? [] : await store.ApplyObservationsAsync(changes, token);
        var committedPaths = changes.Where((_, index) => accepted[index]).Select(change => change.Observation.Item.Path)
            .Concat(renames.Select(rename => rename.Destination.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (committedPaths.Length > 0) onCommitted();
        // Once a transaction commits, report it even if its generation is cancelled before UI publication.
        IReadOnlyList<SavedMediaItem> committed = committedPaths.Length == 0 ? [] :
            (await store.GetItemsByPathsAsync(committedPaths, CancellationToken.None)).Values.ToArray();
        var committedRenames = renames.Select(rename =>
        {
            var destination = committed.FirstOrDefault(item => item.Path.Equals(rename.Destination.Path, StringComparison.OrdinalIgnoreCase));
            return destination is null ? rename : new CatalogExternalRename(rename.Source, destination);
        }).ToArray();
        if (committed.Count > 0) await publish(new(committed, committedRenames, [], false, CatalogChanged: true));
        return committedPaths.Length > 0;
    }

    public static SavedMediaItem Available(FileSystemProbeResult result)
    {
        var hidden = PhotoScanner.IsIgnoredPath(result.Path, false);
        for (string? part = result.Path; part is not null; part = Path.GetDirectoryName(part))
        {
            try { hidden |= (File.GetAttributes(part) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { break; }
        }
        return new SavedMediaItem
        {
            Path = result.Path, Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = result.CheckedAtUtc,
            FileIdentity = result.FileIdentity, SizeBytes = result.Length ?? 0, FileModifiedAt = result.LastWriteTimeUtc?.ToLocalTime(),
            IsVideo = PhotoItem.IsVideoPath(result.Path), IsHiddenOrSystem = hidden
        };
    }
    private static SavedMediaItem Unavailable(string path, FileSystemProbeResult result) => new()
    {
        Path = path, Availability = result.Availability, AvailabilityCheckedAtUtc = result.CheckedAtUtc,
        AvailabilityErrorCode = result.ErrorCode
    };
    private static bool Equivalent(SavedMediaItem old, SavedMediaItem observed) => old.Availability == observed.Availability &&
        old.AvailabilityErrorCode == observed.AvailabilityErrorCode &&
        (observed.Availability != FileAvailability.Available || old.SizeBytes == observed.SizeBytes &&
            old.FileModifiedAt?.ToUniversalTime() == observed.FileModifiedAt?.ToUniversalTime() && old.FileIdentity == observed.FileIdentity &&
            old.IsHiddenOrSystem == observed.IsHiddenOrSystem && old.Path == observed.Path);
    public static bool IsUnder(string path, string root) => StoragePrivacyPolicy.IsUnder(path, root);
}
