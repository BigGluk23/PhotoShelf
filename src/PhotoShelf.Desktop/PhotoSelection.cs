using System.Collections;
namespace PhotoShelf.Desktop;

internal sealed class PhotoSelection : ICollection<PhotoItem>
{
    private readonly Dictionary<string, PhotoItem> _items = new(StringComparer.OrdinalIgnoreCase);
    public PhotoItem? Find(string path) => _items.GetValueOrDefault(path);
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public void Add(PhotoItem item) => _items[item.Path] = item;
    public void Clear() => _items.Clear();
    public bool Contains(PhotoItem item) => _items.ContainsKey(item.Path);
    public void CopyTo(PhotoItem[] array, int index) => _items.Values.CopyTo(array, index);
    public bool Remove(PhotoItem item) => _items.Remove(item.Path);
    public IEnumerator<PhotoItem> GetEnumerator() => _items.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
