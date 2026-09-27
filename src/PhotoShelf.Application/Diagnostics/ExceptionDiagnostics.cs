using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoShelf.Application.Diagnostics;

public sealed record ExceptionDiagnostic(string RootCause, string Details);

/// <summary>Formats a local diagnostic without changing the exception or reading user files.</summary>
public static class ExceptionDiagnostics
{
    public static ExceptionDiagnostic Create(Exception exception, string context, string version)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);

        var causes = new List<string>();
        CollectCauses(exception, causes);
        var rootCause = string.Join(Environment.NewLine, causes.Distinct(StringComparer.Ordinal));
        var details = new StringBuilder()
            .AppendLine("PhotoShelf diagnostic")
            .Append("UTC: ").AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append("Version: ").AppendLine(version)
            .Append("Context: ").AppendLine(context)
            .Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription)
            .Append("OS: ").AppendLine(RuntimeInformation.OSDescription)
            .Append("OS architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString())
            .Append("Process architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
            .AppendLine()
            .AppendLine("Exception (including inner exceptions and stack traces):")
            .Append(exception.ToString())
            .ToString();
        return new ExceptionDiagnostic(rootCause, details);
    }

    private static void CollectCauses(Exception exception, List<string> causes)
    {
        if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) CollectCauses(inner, causes);
        }
        else if (exception.InnerException is { } inner)
        {
            CollectCauses(inner, causes);
        }
        else
        {
            causes.Add($"{exception.GetType().FullName}: {exception.Message}");
        }
    }
}
