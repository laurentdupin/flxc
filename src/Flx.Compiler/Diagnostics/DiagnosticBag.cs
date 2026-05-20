namespace Flx.Compiler.Diagnostics;

internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Count > 0;

    public void Report(string id, string message, SourceLocation? location = null)
    {
        _diagnostics.Add(new Diagnostic(id, message, location));
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
}
