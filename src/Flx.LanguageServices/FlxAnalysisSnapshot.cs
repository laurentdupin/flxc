namespace Flx.LanguageServices;

public sealed class FlxAnalysisSnapshot
{
    private readonly IReadOnlyDictionary<string, FlxSymbolDefinition> _definitionsByKey;
    private readonly IReadOnlyDictionary<string, FlxSymbolInfo> _symbolInfosByKey;
    private readonly IReadOnlyDictionary<string, string> _sourceTextsByPath;
    private readonly IReadOnlyList<FlxReference> _references;

    internal FlxAnalysisSnapshot(
        IReadOnlyList<FlxDiagnostic> diagnostics,
        IReadOnlyList<FlxDocumentSymbol> documentSymbols,
        IReadOnlyList<FlxReference> references,
        IReadOnlyDictionary<string, FlxSymbolDefinition> definitionsByKey,
        IReadOnlyDictionary<string, FlxSymbolInfo> symbolInfosByKey,
        IReadOnlyDictionary<string, string> sourceTextsByPath)
    {
        Diagnostics = diagnostics;
        DocumentSymbols = documentSymbols;
        _references = references;
        _definitionsByKey = definitionsByKey;
        _symbolInfosByKey = symbolInfosByKey;
        _sourceTextsByPath = sourceTextsByPath;
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
        var fullPath = Path.GetFullPath(path);
        var reference = FindReference(fullPath, line, character, includeSameLineFallback: false);
        if (reference is not null &&
            _symbolInfosByKey.TryGetValue(reference.TargetKey, out var info))
        {
            return new FlxHoverResult(FormatHover(info), reference.Range);
        }

        return TryGetBuiltinHover(fullPath, line, character);
    }

    public IReadOnlyList<FlxCompletionItem> GetCompletions(string path, int line, int character)
    {
        return [];
    }

    private FlxReference? FindReference(
        string fullPath,
        int line,
        int character,
        bool includeSameLineFallback = true)
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

        if (!includeSameLineFallback)
            return null;

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

    private FlxHoverResult? TryGetBuiltinHover(string fullPath, int line, int character)
    {
        if (!_sourceTextsByPath.TryGetValue(fullPath, out var text))
            return null;

        var token = TryReadToken(text, line, character);
        if (token is null)
            return null;

        var (name, range) = token.Value;
        var hover = name switch
        {
            "argc" => "```flx\nargc : i32\n```\nProgram argument count.",
            "argv" => "```flx\nargv : Array<string>\n```\nProgram argument values.",
            "string" => "```flx\nstring\n```\nBuilt-in string type. Use `.c_str()` for C interop.",
            "Array" => "```flx\nArray<T>\n```\nBuilt-in generic array type.",
            "void" => "```flx\nvoid\n```\nNo value.",
            "i32" => "```flx\ni32\n```\nBuilt-in 32-bit signed integer.",
            "usize" => "```flx\nusize\n```\nBuilt-in pointer-sized unsigned integer.",
            "f32" => "```flx\nf32\n```\nBuilt-in 32-bit floating-point value.",
            "f64" => "```flx\nf64\n```\nBuilt-in 64-bit floating-point value.",
            "null" => "```flx\nnull\n```\nNull pointer value for C interop.",
            "true" => "```flx\ntrue\n```\nBoolean true.",
            "false" => "```flx\nfalse\n```\nBoolean false.",
            _ => null
        };

        return hover is null ? null : new FlxHoverResult(hover, range);
    }

    private static string FormatHover(FlxSymbolInfo info)
    {
        var lines = new List<string>
        {
            "```flx",
            info.Display,
            "```"
        };

        if (!string.IsNullOrWhiteSpace(info.Detail))
            lines.Add(info.Detail);

        if (!string.IsNullOrWhiteSpace(info.PackageName))
            lines.Add($"package {info.PackageName}");

        if (!string.IsNullOrWhiteSpace(info.ModuleName))
            lines.Add($"module {info.ModuleName}");

        return string.Join("\n", lines);
    }

    private static (string Name, FlxRange Range)? TryReadToken(string text, int line, int character)
    {
        if (!TryGetLine(text, line, out var lineText))
            return null;

        if (lineText.Length == 0)
            return null;

        var index = Math.Clamp(character, 0, lineText.Length - 1);
        if (!IsIdentifierPart(lineText[index]) && index > 0 && IsIdentifierPart(lineText[index - 1]))
            index--;

        if (!IsIdentifierPart(lineText[index]))
            return null;

        var start = index;
        while (start > 0 && IsIdentifierPart(lineText[start - 1]))
            start--;

        var end = index + 1;
        while (end < lineText.Length && IsIdentifierPart(lineText[end]))
            end++;

        var name = lineText[start..end];
        return (name, new FlxRange(new FlxPosition(line, start), new FlxPosition(line, end)));
    }

    private static bool TryGetLine(string text, int line, out string lineText)
    {
        lineText = "";
        if (line < 0)
            return false;

        var currentLine = 0;
        var start = 0;
        for (var position = 0; position <= text.Length; position++)
        {
            if (position < text.Length && text[position] != '\n')
                continue;

            if (currentLine == line)
            {
                var end = position;
                if (end > start && text[end - 1] == '\r')
                    end--;

                lineText = text[start..end];
                return true;
            }

            currentLine++;
            start = position + 1;
        }

        return false;
    }

    private static bool IsIdentifierPart(char value)
    {
        return value == '_' || char.IsLetterOrDigit(value);
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
