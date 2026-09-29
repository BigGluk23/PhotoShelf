using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoShelf.Application.Files;

public sealed record LegacyJournalFile(string Source, string Destination, string Status, string? Problem);
public sealed record LegacyJournalReview(string JournalPath, string JournalHash, IReadOnlyList<LegacyJournalFile> Files)
{
    public bool CanAcknowledge => Files.Count > 0 && Files.All(file => file.Problem is null);
}

/// <summary>Read-only verification of v0.9.7 evidence. Never executes or reverses legacy moves.</summary>
public static class LegacyJournalReviewService
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private sealed record Receipt(int Version, string JournalHash, int Files, DateTime VerifiedUtc);
    private sealed record Snapshot(string Hash, IReadOnlyList<MoveJournalEntry> Entries);
    private static readonly HashSet<string> Fields = ["Source", "Destination", "Staging", "Status", "Hash", "Error", "Temporary"];
    private static readonly HashSet<string> Statuses = ["planned", "staged", "copied_verified", "catalog_committed", "completed", "reconciliation_required", "failed"];

    public static Task<LegacyJournalReview> ReviewAsync(string journalPath, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            using var input = Open(journalPath);
            return await VerifyAsync(journalPath, Read(input, token), token);
        }, token);

    public static Task AcknowledgeCompletedAsync(string journalPath, CancellationToken token = default) => Task.Run(async () =>
    {
        // Re-verify after the confirmation dialog; a previous preview grants no authority over changed files.
        using var input = Open(journalPath);
        var snapshot = Read(input, token);
        var review = await VerifyAsync(journalPath, snapshot, token);
        if (!review.CanAcknowledge) throw new IOException("Не все файлы подтверждены; старый журнал остаётся заблокированным");
        token.ThrowIfCancellationRequested();
        var destination = ReceiptPath(journalPath, snapshot.Hash);
        RejectLinks(destination);
        if (IsAcknowledged(journalPath, snapshot)) return;
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, snapshot.Hash, snapshot.Entries.Count, DateTime.UtcNow));
        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(true); }
        token.ThrowIfCancellationRequested();
        NativeRename.MoveExclusive(partial, destination);
        NativeRename.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
    }, token);

    internal static MoveOperationHistory? TryReadHistory(string path, CancellationToken token)
    {
        try
        {
            using var input = Open(path);
            var snapshot = Read(input, token);
            var acknowledged = IsAcknowledged(path, snapshot);
            return new(path, File.GetCreationTimeUtc(path), snapshot.Entries.Count,
                acknowledged ? snapshot.Entries.Count : 0, acknowledged ? 0 : snapshot.Entries.Count,
                false, false, null,
                acknowledged ? null : "Старый журнал: требуется проверка файлов; автоматическое восстановление отключено",
                IsLegacy: true, LegacyReviewed: acknowledged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static bool IsAcknowledged(string path, Snapshot snapshot)
    {
        try
        {
            var receiptPath = ReceiptPath(path, snapshot.Hash);
            using var input = Open(receiptPath);
            if (input.Length > 4096) return false;
            var receipt = JsonSerializer.Deserialize<Receipt>(input);
            return receipt is { Version: 1 } && receipt.JournalHash == snapshot.Hash &&
                receipt.Files == snapshot.Entries.Count && snapshot.Entries.All(entry => entry.Status == "completed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static string ReceiptPath(string path, string hash) => path + ".reviewed-" + hash + ".json";
    private static FileStream Open(string path)
    {
        RejectLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    }

    private static Snapshot Read(FileStream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (input.Length is <= 0 or > MaxBytes) throw new IOException("Старый журнал требует отдельного разбора: превышен безопасный размер");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (bytes[^1] != (byte)'\n') throw new IOException("Незавершённая запись старого журнала; данные сохранены");
        var latest = new Dictionary<string, MoveJournalEntry>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = bytes.AsSpan(offset).IndexOf((byte)'\n');
            if (count <= 0 || count > 256 * 1024) throw new IOException("Некорректная запись старого журнала");
            using var document = JsonDocument.Parse(bytes.AsMemory(offset, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new IOException("Неизвестный формат журнала");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != Fields.Count || properties.Any(property => !Fields.Contains(property.Name)) ||
                properties.Select(property => property.Name).Distinct().Count() != Fields.Count)
                throw new IOException("Это не журнал v0.9.7; автоматическая проверка отключена");
            var entry = root.Deserialize<MoveJournalEntry>() ?? throw new IOException("Пустая запись");
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Destination) ||
                string.IsNullOrWhiteSpace(entry.Staging) || !Statuses.Contains(entry.Status)) throw new IOException("Неполная запись старого журнала");
            if (latest.TryGetValue(entry.Source, out var previous) &&
                (previous.Destination != entry.Destination || previous.Staging != entry.Staging || previous.Temporary != entry.Temporary))
                throw new IOException("Пути меняются внутри старого журнала; требуется ручная проверка");
            latest[entry.Source] = entry;
            if (latest.Count > DurableMoveJournal.MaxOperationFiles) throw new IOException("Слишком много файлов в старом журнале");
            offset += count + 1;
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes)), latest.Values.ToArray());
    }

    private static async Task<LegacyJournalReview> VerifyAsync(string path, Snapshot snapshot, CancellationToken token)
    {
        var files = new List<LegacyJournalFile>();
        foreach (var entry in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            string? problem = null;
            try
            {
                if (entry.Status != "completed") throw new IOException("Перенос не был завершён; нужен ручной разбор");
                if (entry.Hash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) throw new IOException("Нет проверенного SHA-256");
                if (!Path.IsPathFullyQualified(entry.Source) || !Path.IsPathFullyQualified(entry.Destination) ||
                    string.Equals(Path.GetFullPath(entry.Source), Path.GetFullPath(entry.Destination), StringComparison.OrdinalIgnoreCase) ||
                    !IsTemporary(entry.Staging, entry.Source + ".photoshelf-moving-") ||
                    entry.Temporary is null || !IsTemporary(entry.Temporary, entry.Destination + ".photoshelf-copy-"))
                    throw new IOException("Неоднозначные пути файлов");
                if (!Directory.Exists(Path.GetDirectoryName(entry.Source)))
                    throw new IOException("Папка исходного файла недоступна; отсутствие оригинала не подтверждено");
                foreach (var absent in new[] { entry.Source, entry.Staging, entry.Temporary })
                {
                    RejectLinks(absent);
                    // GetAttributes distinguishes absence from access denied; File.Exists would hide errors.
                    try { File.GetAttributes(absent); throw new IOException("Сохранился исходный или временный файл: " + absent); }
                    catch (FileNotFoundException) { }
                }
                using var destination = Open(entry.Destination);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(destination, token));
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Содержимое файла назначения не совпадает с SHA-256 журнала");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { problem = ex.Message; }
            files.Add(new(entry.Source, entry.Destination, entry.Status, problem));
        }
        return new(path, snapshot.Hash, files);
    }

    private static bool IsTemporary(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(path[prefix.Length..], "N", out _);

    internal static void RejectLinks(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Проверка через ссылки или точки перенаправления запрещена");
    }
}
