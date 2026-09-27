namespace PhotoShelf.Desktop;

public sealed record DuplicateGroup(long SizeBytes, string Hash, IReadOnlyList<PhotoItem> Items);
