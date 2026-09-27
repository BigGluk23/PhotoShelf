using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// These are actual Windows/WPF dispatcher tests. Cross-compilation on macOS is not an execution result.
public sealed class VirtualPhotoRowsTests
{
    [Fact]
    public Task RealListBoxRealizesOnlyViewportOfHundredThousandItemView() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var created = 0;
        using var rows = fixture.Rows((saved, index) => { created++; return Item(saved, index); }, logicalCount: 100_000);
        var list = new ListBox { ItemsSource = rows, Height = 360, Width = 400 };
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        list.SetValue(ScrollViewer.CanContentScrollProperty, true);
        list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        list.ItemContainerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 45d));
        var window = new Window { Content = list, Width = 450, Height = 420, ShowInTaskbar = false };
        try
        {
            window.Show();
            await rows.PrimeAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.Equal(25_001, rows.Count);
            Assert.InRange(created, 1, 4 * 24 * 4);
            Assert.True(rows.CachedRowCount < 200, $"ListBox materialized {rows.CachedRowCount} rows.");
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(10_000));
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FastScrollRequestsKeepPendingPagesAndPlaceholdersBounded() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item, logicalCount: 100_000);
        for (var page = 0; page < 1000; page++)
        {
            _ = rows[1 + page * 24];
            Assert.InRange(rows.PendingPageCount, 0, 8);
        }
        Assert.InRange(rows.CachedRowCount, 0, 512);
        await WaitUntilAsync(() => rows.PendingPageCount == 0);
        Assert.InRange(rows.CachedPageCount, 0, 12);
        Assert.InRange(rows.CachedRowCount, 0, 1024);
    });

    [Fact]
    public Task ParentCancellationPreventsStalePageFromCreatingViewItems() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var created = 0;
        using var rows = fixture.Rows((saved, index) => { created++; return Item(saved, index); }, cancellation.Token);
        _ = rows[1];
        cancellation.Cancel(); // LoadPage yields before querying; this models an immediate new selection.
        await rows.PrimeAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, created);
        Assert.Empty(rows.LoadedItems);
    });

    [Fact]
    public Task DisposalWhilePageIsQueuedPreventsLateResults() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var created = 0;
        var rows = fixture.Rows((saved, index) => { created++; return Item(saved, index); });
        var pending = rows.PrimeAsync();
        rows.Dispose();
        await pending;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, created);
        Assert.Empty(rows.LoadedItems);
    });

    [Fact]
    public Task VisitingManyPagesEvictsDataButCanReloadEarlierPage() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        var first = (PhotoRow)rows[1]!;
        await WaitUntilAsync(() => first.Items.Count > 0);
        var expectedFirst = first.Items[0].Path;
        for (var page = 1; page < 17; page++)
        {
            var row = (PhotoRow)rows[1 + page * 24]!;
            await WaitUntilAsync(() => row.Items.Count > 0);
        }
        Assert.InRange(rows.CachedPageCount, 1, 12);
        Assert.True(rows.LoadedItems.Count() <= 12 * 24 * 4);
        var reloaded = (PhotoRow)rows[1]!;
        await WaitUntilAsync(() => reloaded.Items.Count > 0);
        Assert.Equal(expectedFirst, reloaded.Items[0].Path);
        Assert.Equal(0, reloaded.Items[0].ViewIndex);
    });

    [Fact]
    public Task CollapsedGroupsExposeHeaderWithoutLoadingMedia() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var created = 0;
        using var rows = new VirtualPhotoRows(fixture.Store, fixture.Query, fixture.Groups, 4, 100,
            fixture.Groups.Select(group => group.Key).ToHashSet(), (saved, index) => { created++; return Item(saved, index); });
        await rows.PrimeAsync();
        Assert.Equal(fixture.Groups.Count, rows.Count);
        Assert.True(((PhotoRow)rows[0]!).IsHeader);
        Assert.Equal(0, rows.RowForItem(100));
        Assert.Equal(0, created);
    });

    [Fact]
    public Task RetryAfterTransientCatalogFailureUsesSamePlaceholder() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        var database = Path.Combine(fixture.Directory, "catalog-v2.sqlite");
        // Rename just the table in this isolated test database; the file and real media are never touched.
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE desktop_media_items RENAME TO temporarily_unavailable;";
        await command.ExecuteNonQueryAsync();
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rows.LoadFailed += _ => error.TrySetResult();
        var placeholder = (PhotoRow)rows[1]!;
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(placeholder.Items);
        command.CommandText = "ALTER TABLE temporarily_unavailable RENAME TO desktop_media_items;";
        await command.ExecuteNonQueryAsync();
        await Task.Delay(1100); // Retry backoff is an intentional application contract.
        Assert.Same(placeholder, rows[1]);
        await WaitUntilAsync(() => placeholder.Items.Count > 0);
    });

    [Fact]
    public Task MetadataBatchKeepsRowsCardsAndSelectionWithoutCollectionReset() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        await rows.PrimeAsync();
        var row = (PhotoRow)rows[1]!;
        var before = row.Items.ToArray();
        var selected = before[0]; selected.IsSelected = true;
        var collectionChanges = 0;
        row.Items.CollectionChanged += (_, _) => collectionChanges++;
        var dateChanges = 0;
        selected.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PhotoItem.CaptureDate)) dateChanges++; };
        var update = new SavedMediaItem { Path = selected.Path, SizeBytes = selected.FileSizeBytes,
            FileModifiedAt = selected.FileModifiedAt, CaptureDate = new DateTime(2021, 3, 4), MetadataIndexed = true };
        for (var batch = 0; batch < 100; batch++) rows.ApplyMetadata([update]);
        row.SetItems(before);
        Assert.Same(row, rows[1]);
        Assert.Equal(before, row.Items.ToArray());
        Assert.True(selected.IsSelected);
        Assert.Equal(update.CaptureDate, selected.CaptureDate);
        Assert.Equal(1, dateChanges);
        Assert.Equal(0, collectionChanges);
    });

    [Fact]
    public Task LateMetadataForChangedFileCannotOverwriteVisibleCard() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        await rows.PrimeAsync();
        var item = ((PhotoRow)rows[1]!).Items[0];
        rows.ApplyMetadata([new SavedMediaItem { Path = item.Path, SizeBytes = item.FileSizeBytes + 1,
            FileModifiedAt = item.FileModifiedAt, CaptureDate = new DateTime(2020, 1, 1), MetadataIndexed = true }]);
        Assert.False(item.IsCaptureDateLoaded);
        Assert.Null(item.CaptureDate);
    });

    [Fact]
    public Task DeepAnchorIsLoadedBeforePublicationWithoutLoadingFirstPage() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        await rows.PrimeAsync(1500);
        var anchor = (PhotoRow)rows[rows.RowForItem(1500)]!;
        Assert.Contains(anchor.Items, item => item.ViewIndex == 1500);
        Assert.All(rows.LoadedItems, item => Assert.InRange(item.ViewIndex, 1440, 1535));
        Assert.Equal(1, rows.CachedPageCount);
    });

    [Fact]
    public Task PrimeFailureIsReportedInsteadOfPublishingEmptyPlaceholderAsReady() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Directory, "catalog-v2.sqlite")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE desktop_media_items RENAME TO unavailable_for_prime;";
        await command.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rows.PrimeAsync());
        Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Empty(rows.LoadedItems);
    });

    [Fact]
    public Task RowReorderRetainsUnchangedCardsWithoutReset() => OnStaAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        using var rows = fixture.Rows(Item);
        await rows.PrimeAsync();
        var row = (PhotoRow)rows[1]!;
        var original = row.Items.ToArray();
        var resets = 0;
        row.Items.CollectionChanged += (_, args) => { if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        row.SetItems([original[2], original[0]]);
        Assert.Same(original[2], row.Items[0]); Assert.Same(original[0], row.Items[1]);
        Assert.Equal(0, resets);
    });

    private static PhotoItem Item(SavedMediaItem saved, long index) => new(saved.Path, saved.SizeBytes, saved.FileModifiedAt) { ViewIndex = index };
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static Task OnStaAsync(Func<Task> body)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(); completed.TrySetResult(); }
                catch (Exception exception) { completed.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-test-" + Guid.NewGuid().ToString("N"));
        public SqliteDesktopCatalogStore Store { get; }
        public CatalogViewQuery Query { get; } = new() { NewestFirst = false };
        public IReadOnlyList<CatalogDateGroup> Groups { get; private set; } = [];
        private Fixture() => Store = new(Directory);
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Store.InitializeAsync();
            await fixture.Store.UpsertItemsAsync(Enumerable.Range(0, 2000).Select(i => new SavedMediaItem
            {
                Path = $@"C:\PhotoShelf-test-only\image-{i:D6}.jpg", SizeBytes = 10,
                FileModifiedAt = new DateTime(2024, 6, 1, 12, 0, 0).AddSeconds(i)
            }).ToArray());
            fixture.Groups = await fixture.Store.QueryGroupsAsync(fixture.Query);
            return fixture;
        }
        public VirtualPhotoRows Rows(Func<SavedMediaItem, long, PhotoItem> create, CancellationToken token = default, long? logicalCount = null) =>
            new(Store, Query, logicalCount is null ? Groups : Groups.Select(group => group with { Count = logicalCount.Value }).ToArray(),
                4, 100, new HashSet<string>(), create, token);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
