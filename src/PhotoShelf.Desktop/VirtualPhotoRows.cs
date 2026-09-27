using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

// WPF asks for rows by index. Only requested pages acquire PhotoItem instances.
public sealed class VirtualPhotoRows : IList, IDisposable
{
    private const int RowsPerPage = 24;
    private const int CachedPages = 12;
    private readonly SqliteDesktopCatalogStore _store;
    private readonly CatalogViewQuery _query;
    private readonly Func<SavedMediaItem, long, PhotoItem> _create;
    private readonly CancellationTokenSource _cancel;
    private readonly Dictionary<(int Group, int Page), DateTime> _retryAfter = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly List<Section> _sections = new();
    private readonly Dictionary<int, PhotoRow> _rows = new();
    private readonly Dictionary<(int Group, int Page), Task> _loading = new();
    private readonly LinkedList<(int Group, int Page)> _pages = new();
    private readonly LinkedList<(int Group, int Page)> _deferred = new();
    public int CachedRowCount => _rows.Count;
    public int CachedPageCount => _pages.Count;
    public int PendingPageCount => _loading.Values.Count(x => !x.IsCompleted);
    private readonly int _columns;
    private readonly double _height;
    private bool _disposed;
    public int Count { get; }
    public long ItemCount { get; }
    public event Action<string>? LoadFailed;
    public IEnumerable<PhotoItem> LoadedItems => _rows.Values.SelectMany(x => x.Items);
    public VirtualPhotoRows(SqliteDesktopCatalogStore store, CatalogViewQuery query,
        IReadOnlyList<CatalogDateGroup> groups, int columns, double height,
        ISet<string> collapsed, Func<SavedMediaItem, long, PhotoItem> create, CancellationToken cancellationToken = default)
    {
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _store = store; _query = query; _columns = columns; _height = height; _create = create;
        var row = 0; long item = 0;
        foreach (var group in groups)
        {
            var closed = collapsed.Contains(group.Key);
            var rowCount = closed ? 0 : checked((int)((group.Count + columns - 1) / columns));
            _sections.Add(new Section(group, row, item, rowCount, closed));
            row = checked(row + 1 + rowCount); item += group.Count;
        }
        Count = row; ItemCount = item;
    }
    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_rows.TryGetValue(index, out var cached))
            {
                if (!cached.IsHeader)
                {
                    var group = FindSection(index); var key = (group, (index - _sections[group].FirstRow - 1) / RowsPerPage);
                    if (_pages.Remove(key)) _pages.AddLast(key);
                    if (cached.Items.Count == 0) RequestPage(index);
                }
                return cached;
            }
            var sectionIndex = FindSection(index);
            var section = _sections[sectionIndex];
            PhotoRow row;
            if (index == section.FirstRow)
            {
                var culture = CultureInfo.GetCultureInfo("ru-RU");
                var title = section.Group.Year is null
                    ? (_query.UseCaptureDate ? "Без даты съёмки" : "Без даты файла")
                    : $"{section.Group.Year} / {culture.TextInfo.ToTitleCase(culture.DateTimeFormat.GetMonthName(section.Group.Month!.Value))}";
                row = PhotoRow.CreateHeader($"{title} · {section.Group.Count}", section.Group.Key, section.Collapsed);
            }
            else
            {
                row = new PhotoRow(Array.Empty<PhotoItem>()) { MinimumHeight = _height };
                _rows[index] = row;
                RequestPage(index);
            }
            _rows[index] = row;
            // Fast scrollbar dragging must not accumulate unloaded placeholders.
            if (_rows.Count > 512)
                foreach (var old in _rows.Where(x => x.Key != index && x.Value.Items.Count == 0).Select(x => x.Key).Take(_rows.Count - 512).ToArray()) _rows.Remove(old);
            return row;
        }
        set => throw new NotSupportedException();
    }
    private void RequestPage(int index)
    {
        if (_disposed) return;
        var group = FindSection(index);
        var key = (group, (index - _sections[group].FirstRow - 1) / RowsPerPage);
        if (_loading.ContainsKey(key) || (_retryAfter.TryGetValue(key, out var after) && after > DateTime.UtcNow)) return;
        if (PendingPageCount >= 8)
        {
            _deferred.Remove(key); _deferred.AddLast(key);
            while (_deferred.Count > 8) _deferred.RemoveFirst();
            return;
        }
        _loading[key] = LoadPageAsync(key);
    }
    private int FindSection(int row)
    {
        var low = 0; var high = _sections.Count - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var section = _sections[mid];
            if (row < section.FirstRow) high = mid - 1;
            else if (row > section.FirstRow + section.RowCount) low = mid + 1;
            else return mid;
        }
        throw new ArgumentOutOfRangeException(nameof(row));
    }
    private async Task LoadPageAsync((int Group, int Page) key)
    {
        // Ensure the caller has installed its placeholder before any completion.
        await Task.Yield();
        var entered = false;
        try
        {
            await _gate.WaitAsync(_cancel.Token); entered = true;
            var section = _sections[key.Group];
            var offset = key.Page * RowsPerPage * _columns;
            var page = await _store.QueryPageAsync(_query, offset, RowsPerPage * _columns,
                section.Group.Key, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            for (var i = 0; i < page.Items.Count; i += _columns)
            {
                var rowIndex = section.FirstRow + 1 + key.Page * RowsPerPage + i / _columns;
                // The catalog may grow between the group summary and this page.
                // Its new rows belong to the next projection, never the following header.
                if (rowIndex > section.FirstRow + section.RowCount) break;
                if (!_rows.TryGetValue(rowIndex, out var row))
                    _rows[rowIndex] = row = new PhotoRow(Array.Empty<PhotoItem>()) { MinimumHeight = _height };
                row.SetItems(page.Items.Skip(i).Take(_columns).Select((saved, j) => _create(saved, section.FirstItem + offset + i + j)));
            }
            _pages.Remove(key); _pages.AddLast(key);
            while (_pages.Count > CachedPages)
            {
                var old = _pages.First!.Value; _pages.RemoveFirst();
                var oldSection = _sections[old.Group];
                for (var i = 0; i < RowsPerPage && old.Page * RowsPerPage + i < oldSection.RowCount; i++)
                    _rows.Remove(oldSection.FirstRow + 1 + old.Page * RowsPerPage + i);
                _loading.Remove(old);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _loading.Remove(key); _retryAfter[key] = DateTime.UtcNow.AddSeconds(1); if (!_disposed) LoadFailed?.Invoke(ex.Message); }
        finally
        {
            if (entered) _gate.Release();
            if (!_disposed && !_cancel.IsCancellationRequested && _deferred.Count > 0)
            {
                var next = _deferred.Last!.Value; _deferred.RemoveLast();
                if (!_loading.ContainsKey(next)) _loading[next] = LoadPageAsync(next);
            }
        }
    }
    public async Task PrimeAsync()
    {
        var first = _sections.FindIndex(x => x.RowCount > 0);
        if (first < 0) return;
        _ = this[_sections[first].FirstRow + 1];
        if (_loading.TryGetValue((first, 0), out var task)) await task;
    }
    public long ItemIndexForRow(int rowIndex)
    {
        var section = _sections[FindSection(rowIndex)];
        return section.FirstItem + Math.Max(0, rowIndex - section.FirstRow - 1) * _columns;
    }
    public int RowForItem(long itemIndex)
    {
        foreach (var section in _sections)
            if (itemIndex >= section.FirstItem && itemIndex < section.FirstItem + section.Group.Count)
                return section.Collapsed ? section.FirstRow : section.FirstRow + 1 + (int)((itemIndex - section.FirstItem) / _columns);
        return 0;
    }
    public void Dispose() { _disposed = true; _cancel.Cancel(); _rows.Clear(); _pages.Clear(); }
    public int IndexOf(object? value) => _rows.FirstOrDefault(x => ReferenceEquals(x.Value, value), new KeyValuePair<int, PhotoRow>(-1, null!)).Key;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public IEnumerator GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]!; }
    public void CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    private sealed record Section(CatalogDateGroup Group, int FirstRow, long FirstItem, int RowCount, bool Collapsed);
}
