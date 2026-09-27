using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoShelf.Application.Files;

internal sealed class DurableMoveJournal : IAsyncDisposable
{
    private sealed record Record(int Version, long Sequence, string PreviousChecksum, MoveJournalEntry Entry, string Checksum);
    internal sealed record ReadResult(List<MoveJournalEntry> Entries, long ValidLength, long Sequence, string Checksum, byte[] Tail);
    private readonly FileStream _stream;
    private long _sequence;
    private string _checksum;
    private bool _faulted;
    public List<MoveJournalEntry> Entries { get; }
    private DurableMoveJournal(FileStream stream, ReadResult data)
    { _stream = stream; Entries = data.Entries; _sequence = data.Sequence; _checksum = data.Checksum; }
    public static DurableMoveJournal Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(true);
        NativeRename.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new(stream, new([], 0, 0, "", []));
    }
    public static DurableMoveJournal Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        try
        {
            var data = ReadStream(stream);
            if (data.Tail.Length > 0)
            {
                // Preserve a torn write for diagnosis before appending at the last verified record boundary.
                using var tail = new FileStream(path + ".torn-" + Guid.NewGuid().ToString("N"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                tail.Write(data.Tail); tail.Flush(true);
                stream.SetLength(data.ValidLength); stream.Flush(true);
            }
            stream.Position = stream.Length;
            return new(stream, data);
        }
        catch { stream.Dispose(); throw; }
    }
    public static ReadResult Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadStream(stream);
    }
    private static ReadResult ReadStream(Stream stream)
    {
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var entries = new List<MoveJournalEntry>();
        long sequence = 0; string checksum = ""; int start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            var line = Encoding.UTF8.GetString(bytes, start, i - start);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Пустая запись в журнале операции");
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("Version", out _))
                throw new IOException("Журнал старой версии: автоматическое восстановление отключено; исходные файлы сохранены");
            var record = JsonSerializer.Deserialize<Record>(line) ?? throw new IOException("Нечитаемый журнал");
            if (record.Version != 2 || record.Sequence != sequence + 1 || record.PreviousChecksum != checksum
                || Checksum(record with { Checksum = "" }) != record.Checksum)
                throw new IOException("Нарушена целостность журнала; автоматические изменения остановлены");
            entries.Add(record.Entry); sequence = record.Sequence; checksum = record.Checksum; start = i + 1;
        }
        return new(entries, start, sequence, checksum, bytes[start..]);
    }
    public async Task AppendAsync(MoveJournalEntry entry)
    {
        if (_faulted) throw new IOException("Запись журнала прервана; требуется восстановление проверенного префикса");
        var record = new Record(2, _sequence + 1, _checksum, entry, "");
        record = record with { Checksum = Checksum(record) };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        try
        {
            await _stream.WriteAsync(bytes, CancellationToken.None);
            _stream.Flush(true);
        }
        catch { _faulted = true; throw; }
        _sequence = record.Sequence; _checksum = record.Checksum; Entries.Add(entry);
    }
    private static string Checksum(Record record) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record))));
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
