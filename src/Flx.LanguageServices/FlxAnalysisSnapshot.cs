namespace Flx.LanguageServices;

public sealed class FlxAnalysisSnapshot
{
    private static readonly IReadOnlyList<FlxCompletionItem> KeywordCompletions =
    [
        new("module", FlxCompletionKind.Keyword),
        new("import", FlxCompletionKind.Keyword, insertText: "import c \"\" as "),
        new("export", FlxCompletionKind.Keyword),
        new("parallel", FlxCompletionKind.Keyword),
        new("component", FlxCompletionKind.Keyword),
        new("prefab", FlxCompletionKind.Keyword),
        new("schedule", FlxCompletionKind.Keyword, insertText: "schedule {\n    run \n}"),
        new("run", FlxCompletionKind.Keyword),
        new("loopto", FlxCompletionKind.Keyword),
        new("breakloop", FlxCompletionKind.Keyword),
        new("flatten", FlxCompletionKind.Keyword),
        new("create", FlxCompletionKind.Keyword),
        new("return", FlxCompletionKind.Keyword),
        new("if", FlxCompletionKind.Keyword),
        new("else", FlxCompletionKind.Keyword),
        new("while", FlxCompletionKind.Keyword),
        new("for", FlxCompletionKind.Keyword),
        new("switch", FlxCompletionKind.Keyword),
        new("case", FlxCompletionKind.Keyword)
    ];

    private static readonly IReadOnlyList<FlxCompletionItem> BuiltinCompletions =
    [
        new("argc", FlxCompletionKind.Variable, "i32", documentation: "Program argument count."),
        new("argv", FlxCompletionKind.Variable, "Array<string>", documentation: "Program argument values."),
        new("void", FlxCompletionKind.Type),
        new("i32", FlxCompletionKind.Type),
        new("usize", FlxCompletionKind.Type),
        new("f32", FlxCompletionKind.Type),
        new("f64", FlxCompletionKind.Type),
        new("string", FlxCompletionKind.Type),
        new("Array", FlxCompletionKind.Type, insertText: "Array<"),
        new("true", FlxCompletionKind.Keyword),
        new("false", FlxCompletionKind.Keyword),
        new("null", FlxCompletionKind.Keyword)
    ];

    private static readonly IReadOnlyList<FlxCompletionItem> StringMemberCompletions =
    [
        new("c_str", FlxCompletionKind.Method, "const char *", "c_str()"),
        new("length", FlxCompletionKind.Method, "usize", "length()")
    ];

    private static readonly IReadOnlyList<FlxCompletionItem> ArrayMemberCompletions =
    [
        new("length", FlxCompletionKind.Method, "usize", "length()")
    ];

    private readonly IReadOnlyDictionary<string, FlxSymbolDefinition> _definitionsByKey;
    private readonly IReadOnlyDictionary<string, FlxSymbolInfo> _symbolInfosByKey;
    private readonly IReadOnlyDictionary<string, string> _sourceTextsByPath;
    private readonly IReadOnlyList<FlxFunctionScope> _functionScopes;
    private readonly IReadOnlyDictionary<string, FlxMemberCompletionSet> _memberCompletionsByType;
    private readonly IReadOnlyList<FlxReference> _references;

    internal FlxAnalysisSnapshot(
        IReadOnlyList<FlxDiagnostic> diagnostics,
        IReadOnlyList<FlxDocumentSymbol> documentSymbols,
        IReadOnlyList<FlxReference> references,
        IReadOnlyDictionary<string, FlxSymbolDefinition> definitionsByKey,
        IReadOnlyDictionary<string, FlxSymbolInfo> symbolInfosByKey,
        IReadOnlyDictionary<string, string> sourceTextsByPath,
        IReadOnlyList<FlxFunctionScope> functionScopes,
        IReadOnlyDictionary<string, FlxMemberCompletionSet> memberCompletionsByType)
    {
        Diagnostics = diagnostics;
        DocumentSymbols = documentSymbols;
        _references = references;
        _definitionsByKey = definitionsByKey;
        _symbolInfosByKey = symbolInfosByKey;
        _sourceTextsByPath = sourceTextsByPath;
        _functionScopes = functionScopes;
        _memberCompletionsByType = memberCompletionsByType;
    }

    public IReadOnlyList<FlxDiagnostic> Diagnostics { get; }
    public IReadOnlyList<FlxDocumentSymbol> DocumentSymbols { get; }

    public FlxDefinitionResult? FindDefinition(string path, int line, int character)
    {
        var fullPath = Path.GetFullPath(path);
        var reference = FindReference(fullPath, line, character);

        if (reference is null)
            return null;

        var locations = reference.EffectiveTargetKeys
            .Select(key => _definitionsByKey.TryGetValue(key, out var definition) ? definition : null)
            .Where(definition => definition is not null)
            .Select(definition => new FlxDefinitionLocation(definition!.Path, definition.Range))
            .DistinctBy(location => Path.GetFullPath(location.Path) + "\u001f" +
                                    location.Range.Start.Line + "\u001f" +
                                    location.Range.Start.Character + "\u001f" +
                                    location.Range.End.Line + "\u001f" +
                                    location.Range.End.Character)
            .ToArray();

        return locations.Length == 0 ? null : new FlxDefinitionResult(locations);
    }

    public FlxHoverResult? GetHover(string path, int line, int character)
    {
        var fullPath = Path.GetFullPath(path);
        var reference = FindReference(fullPath, line, character, includeSameLineFallback: false);
        if (reference is not null &&
            reference.Kind == FlxReferenceKind.ScheduleRunTarget &&
            reference.TargetFullName.Contains('*', StringComparison.Ordinal))
        {
            return new FlxHoverResult(FormatWildcardScheduleHover(reference), reference.Range);
        }

        if (reference is not null &&
            reference.EffectiveTargetKeys.Count > 0 &&
            _symbolInfosByKey.TryGetValue(reference.EffectiveTargetKeys[0], out var info))
        {
            return new FlxHoverResult(FormatHover(info), reference.Range);
        }

        return TryGetBuiltinHover(fullPath, line, character);
    }

    public IReadOnlyList<FlxCompletionItem> GetCompletions(string path, int line, int character)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_sourceTextsByPath.TryGetValue(fullPath, out var text) ||
            !TryGetLine(text, line, out var lineText))
        {
            return [];
        }

        var beforeCursor = lineText[..Math.Clamp(character, 0, lineText.Length)];
        if (TryGetDotTarget(beforeCursor, out var targetExpression))
        {
            return TryResolveExpressionType(fullPath, line, character, targetExpression, out var targetType)
                ? GetMemberCompletions(targetType)
                : [];
        }

        if (IsScheduleRunContext(beforeCursor))
            return UniqueCompletionItems(
                new[] { new FlxCompletionItem("*", FlxCompletionKind.Keyword, "Wildcard schedule target segment") }
                    .Concat(GetSymbolCompletions(onlyFunctions: true)));

        return UniqueCompletionItems(
            KeywordCompletions
                .Concat(BuiltinCompletions)
                .Concat(GetSymbolCompletions(onlyFunctions: false)));
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

    private static string FormatWildcardScheduleHover(FlxReference reference)
    {
        var matches = reference.EffectiveTargetFullNames
            .Order(StringComparer.Ordinal)
            .ToArray();

        var lines = new List<string>
        {
            "```flx",
            $"wildcard schedule target {reference.TargetFullName}",
            "```"
        };

        if (matches.Length == 0)
            return string.Join("\n", lines);

        lines.Add(matches.Length == 1 ? "matches 1 function group:" : $"matches {matches.Length} function groups:");
        foreach (var match in matches.Take(10))
            lines.Add($"- {match}");

        if (matches.Length > 10)
            lines.Add($"- ... {matches.Length - 10} more");

        return string.Join("\n", lines);
    }

    private IReadOnlyList<FlxCompletionItem> GetMemberCompletions(string typeName)
    {
        if (typeName == "string")
            return StringMemberCompletions;

        if (typeName.StartsWith("Array<", StringComparison.Ordinal))
            return ArrayMemberCompletions;

        return _memberCompletionsByType.TryGetValue(typeName, out var completions)
            ? completions.Items
            : [];
    }

    private IEnumerable<FlxCompletionItem> GetSymbolCompletions(bool onlyFunctions)
    {
        var candidates = _symbolInfosByKey.Values
            .Where(info => IsCompletionSymbol(info.Kind, onlyFunctions))
            .OrderBy(info => CompletionSortRank(info.Kind))
            .ThenBy(info => info.FullName, StringComparer.Ordinal)
            .ToArray();

        var shortNameCounts = candidates
            .GroupBy(info => ShortName(info.FullName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var info in candidates)
        {
            var shortName = ShortName(info.FullName);
            var label = shortNameCounts[shortName] == 1 ? shortName : info.FullName;
            yield return new FlxCompletionItem(
                label,
                ToCompletionKind(info.Kind),
                info.Display,
                insertText: label,
                documentation: info.Detail);
        }
    }

    private bool TryResolveExpressionType(
        string fullPath,
        int line,
        int character,
        string expression,
        out string typeName)
    {
        typeName = expression switch
        {
            "argv" => "Array<string>",
            "argc" => "i32",
            _ => ""
        };

        if (typeName.Length > 0)
            return true;

        var parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var scope = FindScope(fullPath, line, character);
        if (scope is null || !scope.VariableTypes.TryGetValue(parts[0], out var resolvedType))
            return false;

        typeName = resolvedType;
        for (var index = 1; index < parts.Length; index++)
        {
            if (!_memberCompletionsByType.TryGetValue(typeName, out var members) ||
                !members.FieldTypes.TryGetValue(parts[index], out resolvedType))
            {
                return false;
            }

            typeName = resolvedType;
        }

        return true;
    }

    private FlxFunctionScope? FindScope(string fullPath, int line, int character)
    {
        return _functionScopes
            .Where(scope => PathsEqual(scope.Path, fullPath) && Contains(scope.BodyRange, line, character))
            .OrderBy(scope => RangeLength(scope.BodyRange))
            .FirstOrDefault();
    }

    private static bool TryGetDotTarget(string beforeCursor, out string targetExpression)
    {
        targetExpression = "";
        var end = beforeCursor.TrimEnd().Length;
        if (end == 0)
            return false;

        var position = end - 1;
        while (position >= 0 && IsIdentifierPart(beforeCursor[position]))
            position--;

        if (position < 0 || beforeCursor[position] != '.')
            return false;

        position--;
        while (position >= 0 && (IsIdentifierPart(beforeCursor[position]) || beforeCursor[position] == '.'))
            position--;

        var dotIndex = beforeCursor.LastIndexOf('.', end - 1);
        targetExpression = beforeCursor[(position + 1)..dotIndex].Trim('.');
        return targetExpression.Length > 0;
    }

    private static bool IsScheduleRunContext(string beforeCursor)
    {
        var trimmed = beforeCursor.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        var lastRun = trimmed.LastIndexOf("run", StringComparison.Ordinal);
        if (lastRun < 0)
            return false;

        var beforeRun = lastRun == 0 ? '\0' : trimmed[lastRun - 1];
        if (beforeRun != '\0' && IsIdentifierPart(beforeRun))
            return false;

        var afterRun = lastRun + "run".Length;
        return afterRun == trimmed.Length ||
               (afterRun < trimmed.Length && char.IsWhiteSpace(trimmed[afterRun]));
    }

    private static IReadOnlyList<FlxCompletionItem> UniqueCompletionItems(IEnumerable<FlxCompletionItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FlxCompletionItem>();
        foreach (var item in items)
        {
            var key = item.Label + "\u001f" + item.Kind;
            if (seen.Add(key))
                result.Add(item);
        }

        return result;
    }

    private static bool IsCompletionSymbol(FlxSymbolKind kind, bool onlyFunctions)
    {
        if (onlyFunctions)
            return kind is FlxSymbolKind.Function or FlxSymbolKind.Method;

        return kind is FlxSymbolKind.Module or
               FlxSymbolKind.Component or
               FlxSymbolKind.Prefab or
               FlxSymbolKind.Function or
               FlxSymbolKind.Method or
               FlxSymbolKind.Global;
    }

    private static int CompletionSortRank(FlxSymbolKind kind)
    {
        return kind switch
        {
            FlxSymbolKind.Module => 0,
            FlxSymbolKind.Prefab => 1,
            FlxSymbolKind.Component => 2,
            FlxSymbolKind.Global => 3,
            FlxSymbolKind.Function => 4,
            FlxSymbolKind.Method => 5,
            _ => 10
        };
    }

    private static FlxCompletionKind ToCompletionKind(FlxSymbolKind kind)
    {
        return kind switch
        {
            FlxSymbolKind.Module => FlxCompletionKind.Module,
            FlxSymbolKind.Component => FlxCompletionKind.Component,
            FlxSymbolKind.Prefab => FlxCompletionKind.Prefab,
            FlxSymbolKind.Function => FlxCompletionKind.Function,
            FlxSymbolKind.Method => FlxCompletionKind.Method,
            FlxSymbolKind.Global => FlxCompletionKind.Global,
            _ => FlxCompletionKind.Variable
        };
    }

    private static string ShortName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName[(dot + 1)..];
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
