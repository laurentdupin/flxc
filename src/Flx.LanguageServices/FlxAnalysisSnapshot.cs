namespace Flx.LanguageServices;

public sealed class FlxAnalysisSnapshot
{
    private readonly IReadOnlyDictionary<string, FlxSymbolDefinition> _definitionsByKey;
    private readonly IReadOnlyList<FlxReference> _references;

    internal FlxAnalysisSnapshot(
        IReadOnlyList<FlxDiagnostic> diagnostics,
        IReadOnlyList<FlxDocumentSymbol> documentSymbols,
        IReadOnlyList<FlxReference> references,
        IReadOnlyDictionary<string, FlxSymbolDefinition> definitionsByKey)
    {
        Diagnostics = diagnostics;
        DocumentSymbols = documentSymbols;
        _references = references;
        _definitionsByKey = definitionsByKey;
    }

    public IReadOnlyList<FlxDiagnostic> Diagnostics { get; }
    public IReadOnlyList<FlxDocumentSymbol> DocumentSymbols { get; }

    public FlxDefinitionResult? FindDefinition(string path, int line, int character)
    {
        var fullPath = Path.GetFullPath(path);
        var reference = FindReference(fullPath, line, character);

        if (reference is null)
            return null;

        return _definitionsByKey.TryGetValue(reference.TargetKey, out var definition)
            ? new FlxDefinitionResult(definition.Path, definition.Range)
            : null;
    }

    public FlxHoverResult? GetHover(string path, int line, int character)
    {
        return null;
    }

    public IReadOnlyList<FlxCompletionItem> GetCompletions(string path, int line, int character)
    {
        return [];
    }

    private FlxReference? FindReference(string fullPath, int line, int character)
    {
        var exact = _references
            .Where(candidate => PathsEqual(candidate.Path, fullPath) &&
                                Contains(candidate.Range, line, character))
            .OrderBy(candidate => RangeLength(candidate.Range))
            .FirstOrDefault();

        if (exact is not null)
            return exact;

        var nearEdge = _references
            .Where(candidate => PathsEqual(candidate.Path, fullPath) &&
                                candidate.Range.Start.Line == line &&
                                DistanceFromRange(candidate.Range, character) <= 1)
            .OrderBy(candidate => DistanceFromRange(candidate.Range, character))
            .ThenBy(candidate => RangeLength(candidate.Range))
            .FirstOrDefault();

        if (nearEdge is not null)
            return nearEdge;

        var sameLine = _references
            .Where(candidate => PathsEqual(candidate.Path, fullPath) &&
                                candidate.Range.Start.Line == line)
            .Select(candidate => new
            {
                Reference = candidate,
                Distance = DistanceFromRange(candidate.Range, character)
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => RangeLength(candidate.Reference.Range))
            .FirstOrDefault();

        return sameLine is not null && sameLine.Distance <= 32
            ? sameLine.Reference
            : null;
    }

    private static bool Contains(FlxRange range, int line, int character)
    {
        if (line < range.Start.Line || line > range.End.Line)
            return false;

        if (line == range.Start.Line && character < range.Start.Character)
            return false;

        if (line == range.End.Line && character > range.End.Character)
            return false;

        return true;
    }

    private static int RangeLength(FlxRange range)
    {
        return (range.End.Line - range.Start.Line) * 10_000 + range.End.Character - range.Start.Character;
    }

    private static int DistanceFromRange(FlxRange range, int character)
    {
        if (character < range.Start.Character)
            return range.Start.Character - character;

        if (character > range.End.Character)
            return character - range.End.Character;

        return 0;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
