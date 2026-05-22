namespace Flx.Compiler.Diagnostics;

internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    public bool HasWarnings => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

    public void Report(string id, string message, SourceLocation? location = null)
    {
        _diagnostics.Add(new Diagnostic(id, message, location));
    }

    public void ReportWarning(string id, string message, SourceLocation? location = null)
    {
        _diagnostics.Add(new Diagnostic(id, message, location, DiagnosticSeverity.Warning));
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        _diagnostics.AddRange(diagnostics);
    }

    public void PrintTo(TextWriter writer)
    {
        foreach (var diagnostic in _diagnostics)
            writer.WriteLine(diagnostic);
    }

    public void PrintWarningsTo(TextWriter writer)
    {
        foreach (var diagnostic in _diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning))
            writer.WriteLine(diagnostic);
    }
}
