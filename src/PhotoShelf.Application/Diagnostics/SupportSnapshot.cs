using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhotoShelf.Application.Diagnostics;

/// <summary>A support export built from an explicit allowlist; never reads original logs, catalogs, or media.</summary>
public static class SupportSnapshot
{
    public static string Create(string version, bool legacyCatalog, int currentSessionErrors)
    {
        if (version.Length > 100 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
            version = "unknown";
        return JsonSerializer.Serialize(new
        {
            product = "PhotoShelf", version,
            runtime = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Other",
            legacyCatalog, currentSessionErrors = Math.Max(0, currentSessionErrors),
            originalMediaIncluded = false, filePathsIncluded = false, rawLogsIncluded = false,
            note = "Minimal support summary. Raw exception messages, filenames, database contents and computer/user names are excluded."
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
