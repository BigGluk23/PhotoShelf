namespace PhotoShelf.Application.Updates.Installation;

public enum UpdateHelperCommand { Install, Launch, SelfTest }
public sealed record UpdateHelperArguments(UpdateHelperCommand Command, string? RequestPath)
{
    public static UpdateHelperArguments Parse(IReadOnlyList<string> arguments) => arguments.Count switch
    {
        1 when arguments[0] == "--launch" => new(UpdateHelperCommand.Launch, null),
        1 when arguments[0] == "--self-test" => new(UpdateHelperCommand.SelfTest, null),
        2 when arguments[0] == "--install" && Path.IsPathFullyQualified(arguments[1]) =>
            new(UpdateHelperCommand.Install, Path.GetFullPath(arguments[1])),
        _ => throw new ArgumentException("Expected --install <absolute request path>, --launch, or --self-test.")
    };
}
