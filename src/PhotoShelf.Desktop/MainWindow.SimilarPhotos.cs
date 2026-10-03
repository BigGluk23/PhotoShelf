using System.Windows;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private async Task FindSimilarPhotosAsync(DuplicateSearchScope scope)
    {
        if (_duplicateCancellation is not null || _fileOperationActive || !_catalogLoaded || _closing) return;
        if (_hasPendingRecovery)
        {
            System.Windows.MessageBox.Show(
                "Сначала завершите восстановление в окне «Операции».",
                "Восстановление");
            return;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _duplicateCancellation = operation;
        var token = operation.Token;
        var compareA = _duplicateCompareFolderA;
        var compareB = _duplicateCompareFolderB;
        var query = scope == DuplicateSearchScope.CurrentView ? CreateQuery() : new CatalogViewQuery
        {
            IncludeSystemFolders = _includeSystemFolders,
            ShowVideos = false,
            Folder = scope == DuplicateSearchScope.CurrentFolder ? _activeFolder : null,
            ExcludedFolders = scope == DuplicateSearchScope.IncludedFolders
                ? _folderInclusion.ExcludedFolders : Array.Empty<string>(),
            IncludedFolders = scope == DuplicateSearchScope.IncludedFolders
                ? _folderInclusion.IncludedFolders : Array.Empty<string>()
        };
        query = query with { ShowVideos = false, DuplicateCandidatesOnly = false };

        SetDuplicateSearch(true, 1);
        DuplicateProgressBar.IsIndeterminate = true;
        StatusText.Text = "Собираю уже готовый визуальный индекс…";
        try
        {
            await using var session = await SimilarPhotoSearchSession.CreateAsync(
                LocalCatalogStore.CatalogDirectory, token);
            long candidateCount = 0;
            var searchTask = Task.Run(async () =>
            {
                var page = new List<SavedMediaItem>(128);

                async Task FlushAsync()
                {
                    if (page.Count == 0) return;
                    var observed = await _perceptualFingerprintStore.ReadObservedBatchAsync(page, token);
                    var matches = observed.Select(entry =>
                    {
                        var mask = (compareA is not null && IsUnderFolder(entry.Item.Path, compareA) ? 1 : 0) |
                                   (compareB is not null && IsUnderFolder(entry.Item.Path, compareB) ? 2 : 0);
                        return new SimilarPhotoMatch(entry.Item, entry.Fingerprint, mask);
                    }).ToArray();
                    if (matches.Length > 0) await session.AddAsync(matches, token);
                    page.Clear();
                    var candidates = candidateCount;
                    var indexed = session.IndexedItemCount;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                            StatusText.Text = $"Проверяю сходство: {indexed:N0} в индексе / {candidates:N0} просмотрено";
                    }, DispatcherPriority.Background, token);
                }

                await foreach (var saved in _desktopCatalogStore.EnumerateAsync(query, token))
                {
                    if (scope == DuplicateSearchScope.CompareTwoFolders &&
                        !(compareA is not null && IsUnderFolder(saved.Path, compareA)) &&
                        !(compareB is not null && IsUnderFolder(saved.Path, compareB)))
                        continue;
                    candidateCount++;
                    page.Add(saved);
                    if (page.Count == 128) await FlushAsync();
                }
                await FlushAsync();
                await session.CompleteAsync(scope == DuplicateSearchScope.CompareTwoFolders, token);
            }, token);
            _duplicateWorkTask = searchTask;
            await searchTask;
            token.ThrowIfCancellationRequested();

            var indexingInBackground = !_fingerprintTask.IsCompleted;
            if (session.GroupCount == 0)
            {
                var suffix = indexingInBackground
                    ? "\nВизуальный индекс ещё строится в фоне — позже могут появиться новые результаты."
                    : "";
                System.Windows.MessageBox.Show(
                    $"Похожих серий пока не найдено.\nВ индексе: {session.IndexedItemCount:N0} из {candidateCount:N0}.{suffix}",
                    "Похожие фото");
            }
            else
            {
                var review = new SimilarPhotoReviewWindow(session, candidateCount, indexingInBackground)
                {
                    Owner = this
                };
                foreach (var viewer in OwnedWindows.OfType<PhotoViewerWindow>().ToArray()) viewer.Close();
                review.ShowDialog();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Поиск похожих фото отменён";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Поиск похожих фото");
        }
        finally
        {
            if (ReferenceEquals(_duplicateCancellation, operation)) _duplicateCancellation = null;
            SetDuplicateSearch(false, 0);
        }
    }
}
