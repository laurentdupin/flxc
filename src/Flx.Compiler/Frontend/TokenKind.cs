namespace Flx.Compiler.Frontend;

internal enum TokenKind
{
    EndOfFile,
    Unknown,
    Identifier,
    StringLiteral,
    CharLiteral,
    ModuleKeyword,
    ImportKeyword,
    CKeyword,
    AsKeyword,
    VoidKeyword,
    ScheduleKeyword,
    RunKeyword,
    LoopToKeyword,
    ExportKeyword,
    ComponentKeyword,
    PrefabKeyword,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    Comma,
    Semicolon,
    Dot,
    Colon
}
