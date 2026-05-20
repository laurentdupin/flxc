namespace Flx.Compiler.Frontend;

internal enum TokenKind
{
    EndOfFile,
    Unknown,
    Identifier,
    StringLiteral,
    CharLiteral,
    ImportKeyword,
    CKeyword,
    AsKeyword,
    VoidKeyword,
    ScheduleKeyword,
    RunKeyword,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    Comma,
    Semicolon,
    Dot
}
