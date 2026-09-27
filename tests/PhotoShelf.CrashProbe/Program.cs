using System.Text;
using PhotoShelf.Application.Files;

// This executable exists only for abrupt-process-termination tests. It refuses non-test paths.
if (args.Length != 3) throw new ArgumentException("Expected test-root, checkpoint, and copy|rename");
var root = Path.GetFullPath(args[0]);
var directory = new DirectoryInfo(root);
var expectedParent = Path.GetFullPath(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
if (directory.Parent is null || !string.Equals(directory.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar), expectedParent,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
    || !directory.Name.StartsWith("photoshelf-crash-", StringComparison.Ordinal)
    || !Guid.TryParseExact(directory.Name["photoshelf-crash-".Length..], "N", out _)
    || !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
    throw new ArgumentException("Crash probe accepts only its own photoshelf-crash-GUID temporary fixture directory");
var phase = args[1];
if (args[2] is not ("copy" or "rename")) throw new ArgumentException("Invalid transfer mode");
var source = Path.Combine(root, "in", "sample.jpg");
if (!File.Exists(source)) throw new FileNotFoundException("The parent must create the synthetic source fixture", source);
var service = new FileMoveService(new FileMoveOptions
{
    AlwaysCopy = args[2] == "copy",
    Checkpoint = async (current, _) =>
    {
        if (current != phase) return;
        await Console.Out.WriteLineAsync("CHECKPOINT:" + current);
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan); // parent terminates the process; finally blocks never run
    }
});
var plan = service.Plan([new(source, null)], Path.Combine(root, "out"), CollisionPolicy.Skip);
var results = await service.ExecuteAsync(plan, Path.Combine(root, "operations", "move.jsonl"), async entry =>
{
    var bytes = Encoding.UTF8.GetBytes(entry.Source + "\n" + entry.Destination);
    await using var marker = new FileStream(Path.Combine(root, "catalog-committed.txt"), FileMode.CreateNew,
        FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    await marker.WriteAsync(bytes);
    marker.Flush(true);
}, null, CancellationToken.None);
throw new IOException("Requested checkpoint was not reached: " + string.Join("; ", results.Select(result => result.Error ?? "completed")));
