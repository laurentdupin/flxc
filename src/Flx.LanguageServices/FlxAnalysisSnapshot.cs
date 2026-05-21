namespace Flx.LanguageServices;

public sealed class FlxAnalysisSnapshot
{
    internal FlxAnalysisSnapshot(
        IReadOnlyList<FlxDiagnostic> diagnostics,
        IReadOnlyList<FlxDocumentSymbol> documentSymbols)
    {
        Diagnostics = diagnostics;
        DocumentSymbols = documentSymbols;
    }

    public IReadOnlyList<FlxDiagnostic> Diagnostics { get; }
    public IReadOnlyList<FlxDocumentSymbol> DocumentSymbols { get; }

    public FlxDefinitionResult? FindDefinition(string path, int line, int character)
    {
        return null;
    }

    public FlxHoverResult? GetHover(string path, int line, int character)
    {
        return null;
    }

    public IReadOnlyList<FlxCompletionItem> GetCompletions(string path, int line, int character)
    {
        return [];
    }
}
