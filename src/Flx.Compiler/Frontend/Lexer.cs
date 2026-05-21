using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Frontend;

internal sealed class Lexer
{
    private readonly SourceFile _source;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    public Lexer(SourceFile source, DiagnosticBag diagnostics)
    {
        _source = source;
        _diagnostics = diagnostics;
    }

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
                return tokens;
        }
    }

    private Token NextToken()
    {
        SkipTrivia();

        var start = _position;
        var location = _source.GetLocation(start);

        if (IsAtEnd)
            return new Token(TokenKind.EndOfFile, "", null, start, start, location);

        var current = Current;

        if (IsIdentifierStart(current))
            return ReadIdentifierOrKeyword();

        if (current == '"')
            return ReadStringLiteral();

        if (current == '\'')
            return ReadCharLiteral();

        if (current == '#')
        {
            _diagnostics.Report("FLX0401", "unexpected preprocessor directive after preprocessing.", location);
            while (!IsAtEnd && Current is not '\r' and not '\n')
                _position++;
            return NextToken();
        }

        _position++;

        return current switch
        {
            '(' => new Token(TokenKind.LeftParen, "(", null, start, _position, location),
            ')' => new Token(TokenKind.RightParen, ")", null, start, _position, location),
            '{' => new Token(TokenKind.LeftBrace, "{", null, start, _position, location),
            '}' => new Token(TokenKind.RightBrace, "}", null, start, _position, location),
            ',' => new Token(TokenKind.Comma, ",", null, start, _position, location),
            ';' => new Token(TokenKind.Semicolon, ";", null, start, _position, location),
            '.' => new Token(TokenKind.Dot, ".", null, start, _position, location),
            ':' => new Token(TokenKind.Colon, ":", null, start, _position, location),
            _ => UnknownToken(current, start, location)
        };
    }

    private Token UnknownToken(char current, int start, SourceLocation location)
    {
        return new Token(TokenKind.Unknown, current.ToString(), null, start, _position, location);
    }

    private Token ReadIdentifierOrKeyword()
    {
        var start = _position;
        while (!IsAtEnd && IsIdentifierPart(Current))
            _position++;

        var text = _source.Text[start.._position];
        var kind = text switch
        {
            "module" => TokenKind.ModuleKeyword,
            "import" => TokenKind.ImportKeyword,
            "c" => TokenKind.CKeyword,
            "as" => TokenKind.AsKeyword,
            "void" => TokenKind.VoidKeyword,
            "schedule" => TokenKind.ScheduleKeyword,
            "run" => TokenKind.RunKeyword,
            "loopto" => TokenKind.LoopToKeyword,
            "component" => TokenKind.ComponentKeyword,
            "prefab" => TokenKind.PrefabKeyword,
            _ => TokenKind.Identifier
        };

        return new Token(kind, text, text, start, _position, _source.GetLocation(start));
    }

    private Token ReadStringLiteral()
    {
        var start = _position;
        _position++;
        var value = new System.Text.StringBuilder();
        var terminated = false;

        while (!IsAtEnd)
        {
            var current = Current;
            if (current == '"')
            {
                _position++;
                terminated = true;
                break;
            }

            if (current is '\r' or '\n')
                break;

            if (current == '\\' && _position + 1 < _source.Text.Length)
            {
                value.Append(current);
                _position++;
                value.Append(Current);
                _position++;
                continue;
            }

            value.Append(current);
            _position++;
        }

        if (!terminated)
            _diagnostics.Report("FLX0003", "unterminated string literal.", _source.GetLocation(start));

        return new Token(TokenKind.StringLiteral, _source.Text[start.._position], value.ToString(), start, _position, _source.GetLocation(start));
    }

    private Token ReadCharLiteral()
    {
        var start = _position;
        _position++;
        var terminated = false;

        while (!IsAtEnd)
        {
            var current = Current;
            if (current == '\'')
            {
                _position++;
                terminated = true;
                break;
            }

            if (current is '\r' or '\n')
                break;

            if (current == '\\' && _position + 1 < _source.Text.Length)
            {
                _position += 2;
                continue;
            }

            _position++;
        }

        if (!terminated)
            _diagnostics.Report("FLX0004", "unterminated character literal.", _source.GetLocation(start));

        return new Token(TokenKind.CharLiteral, _source.Text[start.._position], null, start, _position, _source.GetLocation(start));
    }

    private void SkipTrivia()
    {
        while (!IsAtEnd)
        {
            if (char.IsWhiteSpace(Current))
            {
                _position++;
                continue;
            }

            if (Current == '/' && Peek(1) == '/')
            {
                _position += 2;
                while (!IsAtEnd && Current is not '\r' and not '\n')
                    _position++;
                continue;
            }

            if (Current == '/' && Peek(1) == '*')
            {
                var start = _position;
                _position += 2;
                var terminated = false;
                while (!IsAtEnd)
                {
                    if (Current == '*' && Peek(1) == '/')
                    {
                        _position += 2;
                        terminated = true;
                        break;
                    }

                    _position++;
                }

                if (!terminated)
                    _diagnostics.Report("FLX0005", "unterminated block comment.", _source.GetLocation(start));
                continue;
            }

            break;
        }
    }

    private bool IsAtEnd => _position >= _source.Text.Length;
    private char Current => IsAtEnd ? '\0' : _source.Text[_position];
    private char Peek(int offset) => _position + offset >= _source.Text.Length ? '\0' : _source.Text[_position + offset];

    internal static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    internal static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
