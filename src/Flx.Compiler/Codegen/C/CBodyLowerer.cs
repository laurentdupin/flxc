using System.Text;
using System.Text.RegularExpressions;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Codegen.C;

internal sealed class CBodyLowerer
{
    private readonly FunctionSymbol _function;
    private readonly ModuleSymbol _module;
    private readonly CompilationModel _model;

    public CBodyLowerer(FunctionSymbol function, ModuleSymbol module, CompilationModel model)
    {
        _function = function;
        _module = module;
        _model = model;
    }

    public string Lower()
    {
        var content = StripOuterBlock(_function.Syntax.BodyText);
        var builder = new StringBuilder();
        var scope = new Scope(null);

        foreach (var parameter in _function.Parameters)
            scope.Declare(parameter.Name, parameter.Type);

        builder.AppendLine("{");
        LowerStatements(content, builder, "    ", scope);
        builder.Append("}");
        return builder.ToString();
    }

    private void LowerStatements(string content, StringBuilder builder, string indent, Scope parentScope)
    {
        var scope = new Scope(parentScope);
        foreach (var statement in ReadStatements(content))
        {
            if (statement.ForHeader is not null)
            {
                var loweredHeader = LowerExpression(statement.ForHeader, scope);
                builder.AppendLine($"{indent}for ({loweredHeader}) {{");
                LowerStatements(statement.Body ?? "", builder, indent + "    ", scope);
                builder.AppendLine($"{indent}}}");
                continue;
            }

            LowerStatement(statement.Text, builder, indent, scope);
        }

        foreach (var cleanup in scope.Cleanups.AsEnumerable().Reverse())
            builder.AppendLine($"{indent}{cleanup}");
    }

    private void LowerStatement(string statement, StringBuilder builder, string indent, Scope scope)
    {
        var trimmed = statement.Trim();
        if (trimmed.Length == 0)
            return;

        var stringLocal = Regex.Match(trimmed, @"^string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\.|[^""\\])*"")\s*;$");
        if (stringLocal.Success)
        {
            var name = stringLocal.Groups["name"].Value;
            var literal = stringLocal.Groups["value"].Value;
            builder.AppendLine($"{indent}flx_string {name} = flx_string_from_static({literal}, {StringLiteralLength(literal)});");
            scope.Declare(name, "string");
            scope.Cleanups.Add($"flx_string_destroy(&{name});");
            return;
        }

        var arrayLocal = Regex.Match(
            trimmed,
            @"^Array\s*<\s*string\s*>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\[(?<items>.*)\]\s*;$",
            RegexOptions.Singleline);
        if (arrayLocal.Success)
        {
            var name = arrayLocal.Groups["name"].Value;
            builder.AppendLine($"{indent}flx_array_string {name};");
            builder.AppendLine($"{indent}flx_array_string_init(&{name});");
            foreach (var literal in ParseStringLiteralList(arrayLocal.Groups["items"].Value))
                builder.AppendLine($"{indent}flx_array_string_push(&{name}, flx_string_from_static({literal}, {StringLiteralLength(literal)}));");

            scope.Declare(name, "Array<string>");
            scope.Cleanups.Add($"flx_array_string_destroy(&{name});");
            return;
        }

        var create = Regex.Match(
            trimmed,
            @"^(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*create\s+(?<create>[A-Za-z_][A-Za-z0-9_]*)\s*;$");
        if (create.Success && create.Groups["type"].Value == create.Groups["create"].Value &&
            _model.PrefabsByName.ContainsKey(create.Groups["type"].Value))
        {
            var prefabName = create.Groups["type"].Value;
            var variableName = create.Groups["name"].Value;
            builder.AppendLine($"{indent}{CTypeNames.ViewType(prefabName)} {variableName} = {CTypeNames.CreateFunction(prefabName)}(world);");
            scope.Declare(variableName, prefabName);
            return;
        }

        var fieldAssignment = Regex.Match(
            trimmed,
            @"^(?<target>[A-Za-z_][A-Za-z0-9_]*)\.(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.+)\s*;$",
            RegexOptions.Singleline);
        if (fieldAssignment.Success &&
            TryResolvePrefabField(scope, fieldAssignment.Groups["target"].Value, fieldAssignment.Groups["field"].Value, out var target, out var field))
        {
            var value = LowerStringSourceExpression(fieldAssignment.Groups["value"].Value.Trim(), scope);
            builder.AppendLine($"{indent}flx_string_assign(&{target}.ptr->{CTypeNames.SafeIdentifier(field.Component.Name)}.{field.Field.Name}, {value});");
            return;
        }

        var rewritten = CBodyRewriter.Rewrite(
            trimmed,
            _module.CImportsByAlias,
            _function.SourceFile,
            _function.Syntax.BodyStart,
            functionRegistry: _model.FunctionRegistry);
        builder.AppendLine($"{indent}{LowerExpression(rewritten, scope)}");
    }

    private string LowerExpression(string expression, Scope scope)
    {
        var lowered = expression;

        lowered = Regex.Replace(
            lowered,
            @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*(?<index>[^\]]+)\s*\]\s*\.c_str\s*\(\s*\)",
            match =>
            {
                var name = match.Groups["name"].Value;
                if (scope.Lookup(name) == "Array<string>")
                    return $"flx_string_c_str(flx_array_string_at(&{name}, {LowerExpression(match.Groups["index"].Value, scope)}))";

                return match.Value;
            });

        lowered = Regex.Replace(
            lowered,
            @"\b(?<target>[A-Za-z_][A-Za-z0-9_]*)\.(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*\.c_str\s*\(\s*\)",
            match =>
            {
                if (TryResolvePrefabField(scope, match.Groups["target"].Value, match.Groups["field"].Value, out var target, out var field))
                    return $"flx_string_c_str(&{target}.ptr->{CTypeNames.SafeIdentifier(field.Component.Name)}.{field.Field.Name})";

                return match.Value;
            });

        lowered = Regex.Replace(
            lowered,
            @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\.c_str\s*\(\s*\)",
            match =>
            {
                var name = match.Groups["name"].Value;
                return scope.Lookup(name) == "string"
                    ? $"flx_string_c_str(&{name})"
                    : match.Value;
            });

        lowered = Regex.Replace(
            lowered,
            @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\.length\s*\(\s*\)",
            match =>
            {
                var name = match.Groups["name"].Value;
                return scope.Lookup(name) switch
                {
                    "Array<string>" => $"flx_array_string_length(&{name})",
                    "string" => $"flx_string_length(&{name})",
                    _ => match.Value
                };
            });

        return lowered;
    }

    private string LowerStringSourceExpression(string expression, Scope scope)
    {
        var arrayIndex = Regex.Match(expression, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*(?<index>.+)\s*\]$");
        if (arrayIndex.Success && scope.Lookup(arrayIndex.Groups["name"].Value) == "Array<string>")
            return $"flx_array_string_at(&{arrayIndex.Groups["name"].Value}, {LowerExpression(arrayIndex.Groups["index"].Value, scope)})";

        if (scope.Lookup(expression) == "string")
            return $"&{expression}";

        return expression;
    }

    private bool TryResolvePrefabField(Scope scope, string variableName, string fieldName, out string target, out PrefabFieldSymbol field)
    {
        target = variableName;
        field = null!;

        var variableType = scope.Lookup(variableName);
        if (variableType is null || !_model.PrefabsByName.TryGetValue(variableType, out var prefab))
            return false;

        var matches = prefab.Fields.Where(candidate => candidate.Field.Name == fieldName).ToArray();
        if (matches.Length != 1)
            return false;

        field = matches[0];
        return true;
    }

    private static IReadOnlyList<BodyStatement> ReadStatements(string content)
    {
        var statements = new List<BodyStatement>();
        var position = 0;

        while (position < content.Length)
        {
            SkipWhitespace(content, ref position);
            if (position >= content.Length)
                break;

            if (TryReadRawBlockStatement(content, ref position, statements, "switch") ||
                TryReadRawBlockStatement(content, ref position, statements, "while") ||
                TryReadRawBlockStatement(content, ref position, statements, "if"))
            {
                continue;
            }

            if (StartsKeyword(content, position, "for"))
            {
                var headerOpen = content.IndexOf('(', position);
                if (headerOpen < 0)
                    break;

                var headerClose = FindMatching(content, headerOpen, '(', ')');
                if (headerClose < 0)
                    break;

                var blockOpen = headerClose + 1;
                SkipWhitespace(content, ref blockOpen);
                if (blockOpen >= content.Length || content[blockOpen] != '{')
                    break;

                var blockClose = FindMatching(content, blockOpen, '{', '}');
                if (blockClose < 0)
                    break;

                statements.Add(new BodyStatement(
                    content[position..(blockClose + 1)],
                    content[(headerOpen + 1)..headerClose],
                    content[(blockOpen + 1)..blockClose]));
                position = blockClose + 1;
                continue;
            }

            var statementEnd = FindStatementEnd(content, position);
            if (statementEnd < 0)
            {
                statements.Add(new BodyStatement(content[position..], null, null));
                break;
            }

            statements.Add(new BodyStatement(content[position..(statementEnd + 1)], null, null));
            position = statementEnd + 1;
        }

        return statements;
    }

    private static bool TryReadRawBlockStatement(string content, ref int position, List<BodyStatement> statements, string keyword)
    {
        if (!StartsKeyword(content, position, keyword))
            return false;

        var headerOpen = content.IndexOf('(', position);
        if (headerOpen < 0)
            return false;

        var headerClose = FindMatching(content, headerOpen, '(', ')');
        if (headerClose < 0)
            return false;

        var blockOpen = headerClose + 1;
        SkipWhitespace(content, ref blockOpen);
        if (blockOpen >= content.Length || content[blockOpen] != '{')
            return false;

        var blockClose = FindMatching(content, blockOpen, '{', '}');
        if (blockClose < 0)
            return false;

        statements.Add(new BodyStatement(content[position..(blockClose + 1)], null, null));
        position = blockClose + 1;
        return true;
    }

    private static int FindStatementEnd(string text, int start)
    {
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(text, i, c);
                continue;
            }

            if (c == '(')
                parenDepth++;
            else if (c == ')')
                parenDepth--;
            else if (c == '[')
                bracketDepth++;
            else if (c == ']')
                bracketDepth--;
            else if (c == ';' && parenDepth == 0 && bracketDepth == 0)
                return i;
        }

        return -1;
    }

    private static int FindMatching(string text, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(text, i, c);
                continue;
            }

            if (c == openChar)
                depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int SkipString(string text, int quoteStart, char quote)
    {
        for (var i = quoteStart + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote)
                return i;
        }

        return text.Length - 1;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    private static bool StartsKeyword(string text, int position, string keyword)
    {
        if (position + keyword.Length > text.Length)
            return false;

        if (!text.AsSpan(position, keyword.Length).Equals(keyword.AsSpan(), StringComparison.Ordinal))
            return false;

        var beforeOk = position == 0 || !IsIdentifierPart(text[position - 1]);
        var after = position + keyword.Length;
        var afterOk = after >= text.Length || !IsIdentifierPart(text[after]);
        return beforeOk && afterOk;
    }

    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static IReadOnlyList<string> ParseStringLiteralList(string text)
    {
        var values = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
                break;

            if (text[position] != '"')
                break;

            var end = SkipString(text, position, '"');
            values.Add(text[position..(end + 1)]);
            position = end + 1;
            SkipWhitespace(text, ref position);
            if (position < text.Length && text[position] == ',')
                position++;
        }

        return values;
    }

    private static int StringLiteralLength(string literal)
    {
        var length = 0;
        for (var i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length - 1)
            {
                i++;
                length++;
                continue;
            }

            length++;
        }

        return length;
    }

    private static string StripOuterBlock(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[(start + 1)..end] : text;
    }

    private sealed record BodyStatement(string Text, string? ForHeader, string? Body);

    private sealed class Scope
    {
        private readonly Scope? _parent;
        private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

        public Scope(Scope? parent)
        {
            _parent = parent;
        }

        public List<string> Cleanups { get; } = [];

        public void Declare(string name, string type)
        {
            _variables[name] = type;
        }

        public string? Lookup(string name)
        {
            return _variables.TryGetValue(name, out var type) ? type : _parent?.Lookup(name);
        }
    }
}
