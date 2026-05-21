using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Frontend;

internal sealed class Parser
{
    private readonly SourceFile _source;
    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    public Parser(SourceFile source, IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var unit = new CompilationUnitSyntax(_source);

        while (Current.Kind != TokenKind.EndOfFile)
        {
            switch (Current.Kind)
            {
                case TokenKind.ImportKeyword:
                    unit.CImports.Add(ParseCImport());
                    break;
                case TokenKind.ScheduleKeyword:
                    unit.Schedules.Add(ParseSchedule());
                    break;
                case TokenKind.ComponentKeyword:
                    unit.Components.Add(ParseComponent());
                    break;
                case TokenKind.PrefabKeyword:
                    unit.Prefabs.Add(ParsePrefab());
                    break;
                case TokenKind.VoidKeyword:
                case TokenKind.Identifier:
                    unit.Functions.Add(ParseFunction());
                    break;
                default:
                    _diagnostics.Report("FLX0001", "expected import, function, or schedule declaration.", Current.Location);
                    Advance();
                    break;
            }
        }

        return unit;
    }

    private CImportSyntax ParseCImport()
    {
        var importToken = Expect(TokenKind.ImportKeyword, "expected 'import'.");
        Expect(TokenKind.CKeyword, "expected 'c' after 'import'.");
        var header = Expect(TokenKind.StringLiteral, "expected C header string literal.");
        Expect(TokenKind.AsKeyword, "expected 'as' after C import header.");
        var alias = ExpectIdentifier("expected C import alias.");

        if (Current.Kind == TokenKind.Semicolon)
            Advance();

        return new CImportSyntax(header.Value ?? "", alias.Text, importToken.Location);
    }

    private ComponentDeclSyntax ParseComponent()
    {
        var componentToken = Expect(TokenKind.ComponentKeyword, "expected 'component'.");
        var name = ExpectIdentifier("expected component name.");
        var (bodyText, bodyStart) = ParseRawBlock("expected component body.", "unterminated component body.");
        return new ComponentDeclSyntax(name.Text, bodyText, bodyStart, componentToken.Location, name.Location);
    }

    private PrefabDeclSyntax ParsePrefab()
    {
        var prefabToken = Expect(TokenKind.PrefabKeyword, "expected 'prefab'.");
        var name = ExpectIdentifier("expected prefab name.");
        var (bodyText, bodyStart) = ParseRawBlock("expected prefab body.", "unterminated prefab body.");
        return new PrefabDeclSyntax(name.Text, bodyText, bodyStart, prefabToken.Location, name.Location);
    }

    private FunctionDeclSyntax ParseFunction()
    {
        var returnType = ParseTypeName("expected function return type.");
        var declarationLocation = Previous.Location;
        var name = ExpectIdentifier("expected function name.");
        Expect(TokenKind.LeftParen, "expected '(' after function name.");
        var parameters = ParseParameterList();
        Expect(TokenKind.RightParen, "expected ')' after function parameters.");

        var (bodyText, bodyStart) = ParseRawBlock("expected function body.", "unterminated function body.");

        return new FunctionDeclSyntax(returnType, name.Text, parameters, bodyText, bodyStart, declarationLocation, name.Location);
    }

    private (string BodyText, int BodyStart) ParseRawBlock(string expectedMessage, string unterminatedMessage)
    {
        var openBrace = Expect(TokenKind.LeftBrace, expectedMessage);
        var bodyText = "";
        var bodyStart = openBrace.Start;

        if (openBrace.Kind != TokenKind.LeftBrace)
            return (bodyText, bodyStart);

        var depth = 1;
        var closeEnd = openBrace.End;

        while (Current.Kind != TokenKind.EndOfFile && depth > 0)
        {
            var token = Advance();
            if (token.Kind == TokenKind.LeftBrace)
                depth++;
            else if (token.Kind == TokenKind.RightBrace)
                depth--;

            closeEnd = token.End;
        }

        if (depth != 0)
            _diagnostics.Report("FLX0006", unterminatedMessage, openBrace.Location);

        var length = Math.Clamp(closeEnd - bodyStart, 0, _source.Text.Length - bodyStart);
        bodyText = _source.Text.Substring(bodyStart, length);
        return (bodyText, bodyStart);
    }

    private IReadOnlyList<ParameterSyntax> ParseParameterList()
    {
        var parameters = new List<ParameterSyntax>();
        if (Current.Kind == TokenKind.RightParen || Current.Kind == TokenKind.EndOfFile)
            return parameters;

        while (Current.Kind != TokenKind.RightParen && Current.Kind != TokenKind.EndOfFile)
        {
            var type = ParseTypeName("expected parameter type.");
            var name = ExpectIdentifier("expected parameter name.");
            parameters.Add(new ParameterSyntax(type, name.Text, name.Location));

            if (Current.Kind != TokenKind.Comma)
                break;

            Advance();
        }

        return parameters;
    }

    private ScheduleDeclSyntax ParseSchedule()
    {
        var scheduleToken = Expect(TokenKind.ScheduleKeyword, "expected 'schedule'.");
        Expect(TokenKind.LeftBrace, "expected '{' after schedule.");
        var steps = new List<RunStepSyntax>();

        while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind != TokenKind.RunKeyword)
            {
                _diagnostics.Report("FLX0007", "expected schedule statement.", Current.Location);
                Advance();
                continue;
            }

            Advance();
            var target = ExpectIdentifier("expected run target name.");
            steps.Add(new RunStepSyntax(target.Text, target.Location));
            Expect(TokenKind.Semicolon, "expected ';' after run statement.");
        }

        Expect(TokenKind.RightBrace, "expected '}' after schedule.");
        return new ScheduleDeclSyntax(steps, scheduleToken.Location);
    }

    private string ParseTypeName(string message)
    {
        if (Current.Kind is TokenKind.Identifier or TokenKind.VoidKeyword)
        {
            var first = Advance().Text;
            if (Current.Kind == TokenKind.Dot)
            {
                Advance();
                var second = ExpectIdentifier("expected identifier after '.'.");
                return $"{first}.{second.Text}";
            }

            return first;
        }

        _diagnostics.Report("FLX0008", message, Current.Location);
        Advance();
        return "void";
    }

    private Token ExpectIdentifier(string message)
    {
        if (Current.Kind == TokenKind.Identifier)
            return Advance();

        _diagnostics.Report("FLX0009", message, Current.Location);
        return new Token(TokenKind.Identifier, "", "", Current.Start, Current.End, Current.Location);
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (Current.Kind == kind)
            return Advance();

        _diagnostics.Report("FLX0010", message, Current.Location);
        return new Token(kind, "", "", Current.Start, Current.End, Current.Location);
    }

    private Token Advance()
    {
        var token = Current;
        if (Current.Kind != TokenKind.EndOfFile)
            _position++;
        return token;
    }

    private Token Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private Token Previous => _tokens[Math.Clamp(_position - 1, 0, _tokens.Count - 1)];
}
