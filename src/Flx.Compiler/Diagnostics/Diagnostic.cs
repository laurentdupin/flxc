namespace Flx.Compiler.Diagnostics;

internal sealed class Diagnostic
{
    public Diagnostic(string id, string message, SourceLocation? location = null)
    {
        Id = id;
        Message = message;
        Location = location;
    }

    public string Id { get; }
    public string Message { get; }
    public SourceLocation? Location { get; }

    public override string ToString()
    {
        if (Location is { } location)
            return $"{location}: error {Id}: {Message}";

        return $"error {Id}: {Message}";
    }
}
