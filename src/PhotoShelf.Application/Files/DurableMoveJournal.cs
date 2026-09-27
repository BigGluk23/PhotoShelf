using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoShelf.Application.Files;

internal sealed class DurableMoveJournal : IAsyncDisposable
{
    internal const int MaxOperationFiles = 50_000;
    internal const int MaxGroupMembers = 256;
    private const int MaxRecordBytes = 4 * 1024 * 1024;
    private const long MaxStateBytes = 64L * 1024 * 1024;
    private const long MaxJournalBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxRecords = 10_000_000;
    private const string LimitMessage = "Журнал превышает безопасный бюджет автоматической обработки. Файлы и журнал сохранены. Не обрезайте журнал: сохраните его копию и запросите потоковую миграцию/разбор операции; новые операции выполняйте меньшими партиями.";
    private sealed record Record(int Version, long Sequence, string PreviousChecksum, MoveJournalEntry Entry, string Checksum);
    internal sealed record ReadResult(List<MoveJournalEntry> Entries, long ValidLength, long Sequence, string Checksum, long TailLength);
    private readonly FileStream _stream;
    private readonly Dictionary<string, MoveJournalEntry> _latest = new(StringComparer.OrdinalIgnoreCase);
    private long _stateBytes;
    private long _sequence;
    private string _checksum;
    private bool _faulted;
    // One state per source, including every manifest member; historical phases never accumulate in RAM.
    public IReadOnlyCollection<MoveJournalEntry> Entries => _latest.Values;
    private DurableMoveJournal(FileStream stream, ReadResult data)
    {
        _stream = stream; _sequence = data.Sequence; _checksum = data.Checksum;
        foreach (var entry in data.Entries) Apply(_latest, ref _stateBytes, entry);
    }
    public static DurableMoveJournal Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.WriteThrough);
        stream.Flush(true);
        NativeRename.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new(stream, new([], 0, 0, "", 0));
    }
    public static DurableMoveJournal Open(string path, CancellationToken token = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.WriteThrough);
        try
        {
            var data = ReadStream(stream, token);
            if (data.TailLength > 0)
            {
                // Preserve exactly the torn suffix before truncating; no full-file or tail-sized allocation.
                using var tail = new FileStream(path + ".torn-" + Guid.NewGuid().ToString("N"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Position = data.ValidLength; stream.CopyTo(tail, 65536); tail.Flush(true);
                NativeRename.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                stream.SetLength(data.ValidLength); stream.Flush(true);
            }
            stream.Position = stream.Length;
            return new(stream, data);
        }
        catch { stream.Dispose(); throw; }
    }
    public static ReadResult Read(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
        return ReadStream(stream, token);
    }
    private static ReadResult ReadStream(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream.Length > MaxJournalBytes) throw new IOException(LimitMessage);
        var latest = new Dictionary<string, MoveJournalEntry>(StringComparer.OrdinalIgnoreCase);
        long sequence = 0, validLength = 0, position = 0, stateBytes = 0; string checksum = "";
        var buffer = ArrayPool<byte>.Shared.Rent(MaxRecordBytes);
        var chunk = ArrayPool<byte>.Shared.Rent(65536);
        var length = 0;
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var offset = 0;
                while (offset < read)
                {
                    var newline = chunk.AsSpan(offset, read - offset).IndexOf((byte)'\n');
                    var take = newline < 0 ? read - offset : newline;
                    if (length + take > MaxRecordBytes || position + take > MaxJournalBytes) throw new IOException(LimitMessage);
                    chunk.AsSpan(offset, take).CopyTo(buffer.AsSpan(length));
                    length += take; offset += take; position += take;
                    if (newline < 0) continue;
                    offset++; position++;
                    if (length == 0) throw new IOException("Пустая запись в журнале операции");
                    if (sequence == MaxRecords) throw new IOException(LimitMessage);
                    token.ThrowIfCancellationRequested();
                    var record = JsonSerializer.Deserialize<Record>(buffer.AsSpan(0, length)) ?? throw new IOException("Нечитаемый журнал");
                    if (record.Version != 2) throw new IOException("Журнал старой/неизвестной версии: автоматическое восстановление отключено; исходные файлы сохранены");
                    ValidateSize(record.Entry, writing: false);
                    if (record.Sequence != sequence + 1 || record.PreviousChecksum != checksum || Checksum(record with { Checksum = "" }) != record.Checksum)
                        throw new IOException("Нарушена целостность журнала; автоматические изменения остановлены");
                    Apply(latest, ref stateBytes, record.Entry);
                    sequence = record.Sequence; checksum = record.Checksum; validLength = position; length = 0;
                }
            }
            return new(latest.Values.ToList(), validLength, sequence, checksum, position - validLength);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); ArrayPool<byte>.Shared.Return(chunk, clearArray: true); }
    }
    public async Task AppendAsync(MoveJournalEntry entry)
    {
        if (_faulted) throw new IOException("Запись журнала прервана; требуется восстановление проверенного префикса");
        ValidateSize(entry, writing: true);
        CheckStateBudget(_latest, _stateBytes, entry);
        if (_sequence >= MaxRecords) throw new IOException(LimitMessage);
        var record = new Record(2, _sequence + 1, _checksum, entry, "");
        record = record with { Checksum = Checksum(record) };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        if (bytes.Length > MaxRecordBytes || _stream.Length + bytes.Length > MaxJournalBytes) throw new IOException(LimitMessage);
        try { await _stream.WriteAsync(bytes, CancellationToken.None); _stream.Flush(true); }
        catch { _faulted = true; throw; }
        _sequence = record.Sequence; _checksum = record.Checksum; Apply(_latest, ref _stateBytes, entry);
    }
    private static IEnumerable<MoveJournalEntry> Members(MoveJournalEntry entry) => entry.Members ?? new[] { entry };
    private static void Apply(Dictionary<string, MoveJournalEntry> latest, ref long bytes, MoveJournalEntry entry)
    {
        CheckStateBudget(latest, bytes, entry);
        foreach (var member in Members(entry))
        {
            if (latest.TryGetValue(member.Source, out var previous)) bytes -= Estimate(previous);
            latest[member.Source] = member; bytes += Estimate(member);
        }
    }
    private static void CheckStateBudget(Dictionary<string, MoveJournalEntry> latest, long bytes, MoveJournalEntry entry)
    {
        var additions = 0;
        foreach (var member in Members(entry))
        {
            if (latest.TryGetValue(member.Source, out var previous)) bytes -= Estimate(previous); else additions++;
            bytes += Estimate(member);
        }
        if (latest.Count + additions > MaxOperationFiles || bytes > MaxStateBytes) throw new IOException(LimitMessage);
    }
    private static long Estimate(MoveJournalEntry entry) => 512L + 2L * ((long)entry.Source.Length + entry.Destination.Length + entry.Staging.Length + (entry.Status?.Length ?? 0) +
        (entry.Hash?.Length ?? 0) + (entry.Error?.Length ?? 0) + (entry.Temporary?.Length ?? 0) + (entry.GroupId?.Length ?? 0) +
        (entry.Mode?.Length ?? 0) + (entry.UndoJournalPath?.Length ?? 0) + (entry.RetainedOriginal?.Length ?? 0) + (entry.WindowsSecurityDescriptor?.Length ?? 0));
    private static void ValidateSize(MoveJournalEntry entry, bool writing)
    {
        if (entry is null || entry.Source is null || entry.Destination is null || entry.Staging is null) throw new IOException("Неполная запись журнала");
        // Old larger manifests remain readable within the record/state byte budget.
        if (writing && entry.Members?.Count > MaxGroupMembers) throw new IOException(LimitMessage);
        long estimate = Estimate(entry);
        if (entry.Members is not null)
        {
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in entry.Members)
            {
                if (member is null || member.Members is not null || member.Source is null || member.Destination is null || member.Staging is null)
                    throw new IOException("Вложенный или неполный манифест журнала");
                if (!sources.Add(member.Source)) throw new IOException("Исходный файл повторяется в манифесте");
                estimate += Estimate(member);
                if (estimate > MaxRecordBytes / 3) throw new IOException(LimitMessage);
            }
        }
        // Escaped JSON can expand a UTF-16 character to six ASCII bytes; check before serializing.
        if (estimate > MaxRecordBytes / 3) throw new IOException(LimitMessage);
    }
    private static string Checksum(Record record) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record))));
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
