namespace PhotoShelf.Application.Diagnostics;

public enum StartupCheckKind { None, Resources, UserInterface, UserInterfaceBrowse }

/// <summary>Strict diagnostic entry points. They never accept a caller-selected catalog directory.</summary>
public sealed record StartupCheckArguments(StartupCheckKind Kind, string? ReportPath)
{
    public static StartupCheckArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new(StartupCheckKind.None, null);
        if (args.Count != 3) throw new ArgumentException("Expected one check flag and one report path.");
        var kind = args[0] switch
        {
            "--verify-startup-resources" when args[1] == "--startup-report" => StartupCheckKind.Resources,
            "--ui-smoke" when args[1] == "--smoke-report" => StartupCheckKind.UserInterface,
            "--ui-browse-smoke" when args[1] == "--smoke-report" => StartupCheckKind.UserInterfaceBrowse,
            _ => throw new ArgumentException("Unknown or incompatible startup arguments.")
        };
        if (!Path.IsPathFullyQualified(args[2])) throw new ArgumentException("Report path must be absolute.");
        var path = Path.GetFullPath(args[2]);
        if (File.Exists(path) || Directory.Exists(path)) throw new ArgumentException("Report must be a new file.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Report directory must exist.");
        return new(kind, path);
    }
}
