using System.Text;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Codegen.C;

internal static class CBodyRewriter
{
    public static void ValidateAliases(
        string body,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag diagnostics)
    {
        Rewrite(body, importsByAlias, source, bodyStart, diagnostics);
    }

    public static string Rewrite(
        string body,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag? diagnostics = null)
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
                if (TryRewriteAliasCall(body, output, ref position, importsByAlias, source, bodyStart, diagnostics))
                    continue;
            }

            output.Append(current);
            position++;
        }

        return output.ToString();
    }

    private static bool TryRewriteAliasCall(
        string body,
        StringBuilder output,
        ref int position,
        IReadOnlyDictionary<string, CImportSymbol> importsByAlias,
        SourceFile source,
        int bodyStart,
        DiagnosticBag? diagnostics)
    {
        var identifierStart = position;
        if (IsMemberChainSegment(body, identifierStart))
            return false;

        var identifierEnd = ReadIdentifier(body, identifierStart);
        var afterAliasWhitespace = SkipWhitespace(body, identifierEnd);

        if (afterAliasWhitespace >= body.Length || body[afterAliasWhitespace] != '.')
            return false;

        var afterDotWhitespace = SkipWhitespace(body, afterAliasWhitespace + 1);
        if (afterDotWhitespace >= body.Length || !Lexer.IsIdentifierStart(body[afterDotWhitespace]))
            return false;

        var memberStart = afterDotWhitespace;
        var memberEnd = ReadIdentifier(body, memberStart);
        var afterMemberWhitespace = SkipWhitespace(body, memberEnd);

        var alias = body[identifierStart..identifierEnd];
        if (importsByAlias.ContainsKey(alias))
        {
            output.Append(body[memberStart..memberEnd]);
            position = memberEnd;
            return true;
        }

        if (afterMemberWhitespace < body.Length && body[afterMemberWhitespace] == '(')
        {
            diagnostics?.Report("FLX0200", $"unknown C import alias '{alias}'.", source.GetLocation(bodyStart + identifierStart));
            output.Append(body[identifierStart..memberEnd]);
            position = memberEnd;
            return true;
        }

        return false;
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
