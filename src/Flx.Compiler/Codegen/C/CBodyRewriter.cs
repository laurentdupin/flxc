using System.Text;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Codegen.C;

internal static class CBodyRewriter
{
    private static readonly IReadOnlyDictionary<string, string> ProgramArgumentIdentifierRewrites =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["argc"] = "flx_argc",
            ["argv"] = "flx_argv"
        };

    public static void ValidateAliases(
        string body,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag diagnostics)
    {
        Rewrite(body, importsByAlias, source, bodyStart, diagnostics);
    }

    public static string RewriteProgramArguments(string body)
    {
        return RewriteIdentifiers(body, ProgramArgumentIdentifierRewrites);
    }

    public static string Rewrite(
        string body,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag? diagnostics = null,
        FunctionRegistry? functionRegistry = null,
        ModuleSymbol? currentModule = null)
    {
        var output = new StringBuilder(body.Length);
        var position = 0;

        while (position < body.Length)
        {
            var current = body[position];

            if (current == '"')
            {
                CopyStringLike(body, output, ref position, '"');
                continue;
            }

            if (current == '\'')
            {
                CopyStringLike(body, output, ref position, '\'');
                continue;
            }

            if (current == '/' && Peek(body, position, 1) == '/')
            {
                CopyLineComment(body, output, ref position);
                continue;
            }

            if (current == '/' && Peek(body, position, 1) == '*')
            {
                CopyBlockComment(body, output, ref position);
                continue;
            }

            if (Lexer.IsIdentifierStart(current))
            {
                if (TryRewriteIdentifier(body, output, ref position, importsByAlias, source, bodyStart, diagnostics, functionRegistry, currentModule))
                    continue;
            }

            output.Append(current);
            position++;
        }

        return output.ToString();
    }

    private static string RewriteIdentifiers(string body, IReadOnlyDictionary<string, string> identifierRewrites)
    {
        var output = new StringBuilder(body.Length);
        var position = 0;

        while (position < body.Length)
        {
            var current = body[position];

            if (current == '"')
            {
                CopyStringLike(body, output, ref position, '"');
                continue;
            }

            if (current == '\'')
            {
                CopyStringLike(body, output, ref position, '\'');
                continue;
            }

            if (current == '/' && Peek(body, position, 1) == '/')
            {
                CopyLineComment(body, output, ref position);
                continue;
            }

            if (current == '/' && Peek(body, position, 1) == '*')
            {
                CopyBlockComment(body, output, ref position);
                continue;
            }

            if (Lexer.IsIdentifierStart(current))
            {
                var identifierStart = position;
                var identifierEnd = ReadIdentifier(body, identifierStart);
                var identifier = body[identifierStart..identifierEnd];

                output.Append(!IsMemberChainSegment(body, identifierStart) &&
                              identifierRewrites.TryGetValue(identifier, out var replacement)
                    ? replacement
                    : identifier);

                position = identifierEnd;
                continue;
            }

            output.Append(current);
            position++;
        }

        return output.ToString();
    }

    private static bool TryRewriteIdentifier(
        string body,
        StringBuilder output,
        ref int position,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag? diagnostics,
        FunctionRegistry? functionRegistry,
        ModuleSymbol? currentModule)
    {
        var identifierStart = position;
        var identifierEnd = ReadIdentifier(body, identifierStart);
        var identifier = body[identifierStart..identifierEnd];

        if (IsMemberChainSegment(body, identifierStart))
        {
            output.Append(identifier);
            position = identifierEnd;
            return true;
        }

        var afterAliasWhitespace = SkipWhitespace(body, identifierEnd);

        if (afterAliasWhitespace < body.Length && body[afterAliasWhitespace] == '.')
        {
            var afterDotWhitespace = SkipWhitespace(body, afterAliasWhitespace + 1);
            if (afterDotWhitespace < body.Length && Lexer.IsIdentifierStart(body[afterDotWhitespace]))
            {
                var memberStart = afterDotWhitespace;
                var memberEnd = ReadIdentifier(body, memberStart);
                var memberName = body[memberStart..memberEnd];
                var afterMemberWhitespace = SkipWhitespace(body, memberEnd);

                if (importsByAlias.ContainsKey(identifier))
                {
                    output.Append(memberName);
                    position = memberEnd;
                    return true;
                }

                if (afterMemberWhitespace < body.Length &&
                    body[afterMemberWhitespace] == '(' &&
                    memberName is not ("c_str" or "length" or "empty" or "clone"))
                {
                    if (diagnostics is not null && !IsZeroArgumentCall(body, afterMemberWhitespace))
                        diagnostics.Report("FLX0200", $"unknown C import alias '{identifier}'.", source.GetLocation(bodyStart + identifierStart));
                    output.Append(body[identifierStart..memberEnd]);
                    position = memberEnd;
                    return true;
                }
            }
        }

        if (identifier == "null")
        {
            output.Append("NULL");
            position = identifierEnd;
            return true;
        }

        if (identifier == "breakloop")
        {
            output.Append("flx_schedule_request_break()");
            position = identifierEnd;
            return true;
        }

        if (functionRegistry is not null)
        {
            var matches = functionRegistry.ResolveFunctionGroup(identifier, currentModule, out _);
            if (matches.Count == 1)
            {
                output.Append(matches[0].MangledName);
                position = identifierEnd;
                return true;
            }
        }

        output.Append(identifier);
        position = identifierEnd;
        return true;
    }

    private static bool IsMemberChainSegment(string body, int identifierStart)
    {
        var position = identifierStart - 1;
        while (position >= 0 && char.IsWhiteSpace(body[position]))
            position--;

        return position >= 0 && body[position] == '.';
    }

    private static int ReadIdentifier(string text, int start)
    {
        var position = start;
        while (position < text.Length && Lexer.IsIdentifierPart(text[position]))
            position++;
        return position;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var position = start;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
        return position;
    }

    private static bool IsZeroArgumentCall(string text, int openParen)
    {
        var position = openParen + 1;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;

        return position < text.Length && text[position] == ')';
    }

    private static void CopyStringLike(string body, StringBuilder output, ref int position, char quote)
    {
        output.Append(body[position++]);
        while (position < body.Length)
        {
            var current = body[position];
            output.Append(current);
            position++;

            if (current == '\\' && position < body.Length)
            {
                output.Append(body[position]);
                position++;
                continue;
            }

            if (current == quote)
                return;
        }
    }

    private static void CopyLineComment(string body, StringBuilder output, ref int position)
    {
        output.Append(body[position++]);
        output.Append(body[position++]);

        while (position < body.Length)
        {
            var current = body[position];
            output.Append(current);
            position++;
            if (current is '\r' or '\n')
                return;
        }
    }

    private static void CopyBlockComment(string body, StringBuilder output, ref int position)
    {
        output.Append(body[position++]);
        output.Append(body[position++]);

        while (position < body.Length)
        {
            var current = body[position];
            output.Append(current);
            position++;

            if (current == '*' && position < body.Length && body[position] == '/')
            {
                output.Append(body[position]);
                position++;
                return;
            }
        }
    }

    private static char Peek(string text, int position, int offset)
    {
        var index = position + offset;
        return index >= text.Length ? '\0' : text[index];
    }
}
