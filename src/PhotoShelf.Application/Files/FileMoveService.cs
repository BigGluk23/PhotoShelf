using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoShelf.Application.Files;

public enum CollisionPolicy { Skip, Rename }
public sealed record MoveRequest(string Source, DateTime? CaptureDate);
public sealed record MoveEntry(string Source, string Destination, long Length, DateTime ModifiedUtc, string? SkipReason);
public sealed record MoveResult(MoveEntry Entry, bool Moved, string? Error);
public sealed record MoveJournalEntry(string Source, string Destination, string Staging, string Status, string? Hash, string? Error, string? Temporary = null);

// No WPF/SQLite dependencies. The caller owns confirmation and catalog reconciliation.
public sealed class FileMoveService
{
    public IReadOnlyList<MoveEntry> Plan(IEnumerable<MoveRequest> requests, string destination, CollisionPolicy collision, bool byYear = false)
    {
        destination = Path.GetFullPath(destination);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MoveEntry>();
        foreach (var request in requests)
        {
            var source = Path.GetFullPath(request.Source);
            if (!sources.Add(source)) continue;
            var directory = destination;
            if (byYear)
            {
                directory = request.CaptureDate is { } date
                    ? Path.Combine(directory, date.Year.ToString("D4"), $"{date.Month:D2} {CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetMonthName(date.Month))}")
                    : Path.Combine(directory, "Без даты съёмки");
            }
            var target = Path.Combine(directory, Path.GetFileName(source));
            var file = new FileInfo(source);
            string? reason = !file.Exists ? "Исходный файл недоступен" : source.Equals(target, StringComparison.OrdinalIgnoreCase) ? "Уже в этой папке" : null;
            if (reason is null && (File.Exists(target) || reserved.Contains(target)))
            {
                if (collision == CollisionPolicy.Skip) reason = "Имя занято: пропуск";
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(target);
                    var extension = Path.GetExtension(target);
                    var number = 1;
                    do { target = Path.Combine(directory, $"{stem} ({number++}){extension}"); }
                    while (File.Exists(target) || reserved.Contains(target));
                }
            }
            if (reason is null && new[] { ".aae", ".AAE", ".xmp", ".XMP" }.Any(ext => File.Exists(Path.ChangeExtension(source, ext))))
                reason = "Есть AAE/XMP: требуется перенос связанной группы";
            reserved.Add(target);
            result.Add(new MoveEntry(source, target, file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc : default, reason));
        }
        return result;
    }

    public async Task<IReadOnlyList<MoveResult>> ExecuteAsync(IReadOnlyList<MoveEntry> plan, string journalPath,
        Func<MoveEntry, Task> commitCatalog, IProgress<int>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        // A new operation never overwrites an existing recovery journal.
        await using var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        async Task Log(MoveJournalEntry entry)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
            await journal.WriteAsync(bytes, CancellationToken.None);
            journal.Flush(flushToDisk: true);
        }
        var results = new List<MoveResult>();
        foreach (var entry in plan)
        {
            token.ThrowIfCancellationRequested();
            if (entry.SkipReason is not null) { results.Add(new(entry, false, entry.SkipReason)); continue; }
            var staging = entry.Source + ".photoshelf-moving-" + Guid.NewGuid().ToString("N");
            var temporary = entry.Destination + ".photoshelf-copy-" + Guid.NewGuid().ToString("N");
            string? digest = null;
            var published = false;
            await Log(new(entry.Source, entry.Destination, staging, "planned", null, null, temporary));
            try
            {
                var original = new FileInfo(entry.Source);
                if (!original.Exists || original.Length != entry.Length || original.LastWriteTimeUtc != entry.ModifiedUtc)
                    throw new IOException("Файл изменился после предварительного просмотра");
                if (File.Exists(entry.Destination)) throw new IOException("Целевое имя уже занято");
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                File.Move(entry.Source, staging, overwrite: false);
                await Log(new(entry.Source, entry.Destination, staging, "staged", null, null, temporary));
                await using (var input = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, token);
                        output.Flush(flushToDisk: true);
                    }
                    input.Position = 0;
                    digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                    await using var copy = File.OpenRead(temporary);
                    var copyDigest = Convert.ToHexString(await SHA256.HashDataAsync(copy, token));
                    if (copyDigest != digest) throw new IOException("Проверка скопированного файла не пройдена");
                }
                File.SetLastWriteTimeUtc(temporary, entry.ModifiedUtc);
                File.Move(temporary, entry.Destination, overwrite: false);
                published = true;
                await Log(new(entry.Source, entry.Destination, staging, "copied_verified", digest, null, temporary));
                await commitCatalog(entry);
                await Log(new(entry.Source, entry.Destination, staging, "catalog_committed", digest, null, temporary));
                File.Delete(staging); // A verified destination and durable journal already exist.
                await Log(new(entry.Source, entry.Destination, staging, "completed", digest, null, temporary));
                results.Add(new(entry, true, null));
            }
            catch (Exception ex)
            {
                // Retain the verified destination on catalog failure. Restore the original when possible.
                if (File.Exists(staging) && !File.Exists(entry.Source))
                {
                    try { File.Move(staging, entry.Source, overwrite: false); } catch { }
                }
                await Log(new(entry.Source, entry.Destination, staging, published ? "reconciliation_required" : "failed", digest, ex.Message, temporary));
                results.Add(new(entry, false, ex.Message));
                if (ex is OperationCanceledException) throw;
            }
            progress?.Report(results.Count);
        }
        return results;
    }
}
