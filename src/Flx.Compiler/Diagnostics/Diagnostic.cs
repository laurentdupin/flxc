namespace Flx.Compiler.Diagnostics;

internal enum DiagnosticSeverity
{
    Error,
    Warning
}

internal sealed class Diagnostic
{
    public Diagnostic(
        string id,
        string message,
        SourceLocation? location = null,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        Id = id;
        Message = message;
        Location = location;
        Severity = severity;
    }

    public string Id { get; }
    public string Message { get; }
    public SourceLocation? Location { get; }
    public DiagnosticSeverity Severity { get; }

    public override string ToString()
    {
        var severity = Severity == DiagnosticSeverity.Warning ? "warning" : "error";
        if (Location is { } location)
            return $"{location}: {severity} {Id}: {Message}";

        return $"{severity} {Id}: {Message}";
    }
}
