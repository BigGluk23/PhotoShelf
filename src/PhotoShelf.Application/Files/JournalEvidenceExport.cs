using System.Security.Cryptography;

namespace PhotoShelf.Application.Files;

/// <summary>Copies even an unreadable journal for manual diagnosis, without interpreting or modifying it.</summary>
public static class JournalEvidenceExport
{
    public static Task<string> CopyAsync(string journalDirectory, string journalPath, string destination, CancellationToken token = default) => Task.Run(async () =>
    {
        var source = Path.GetFullPath(journalPath);
        destination = Path.GetFullPath(destination);
        var directory = Path.GetFullPath(journalDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(source), directory, StringComparison.OrdinalIgnoreCase) ||
            !source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only a selected operation journal can be exported.");
        RejectLinks(source); RejectLinks(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // CreateNew never replaces an existing file. An interrupted export remains explicitly .partial.
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        string hash;
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, token); output.Flush(true);
            input.Position = 0; output.Position = 0;
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            var copyHash = Convert.ToHexString(await SHA256.HashDataAsync(output, token));
            if (hash != copyHash) throw new IOException("Journal export verification failed; the original is unchanged.");
        }
        token.ThrowIfCancellationRequested();
        File.Move(partial, destination, overwrite: false);
        return hash;
    }, token);

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Export through links or reparse points is not supported.");
    }
}
