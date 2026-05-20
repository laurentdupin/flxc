using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Frontend;

internal readonly record struct Token(TokenKind Kind, string Text, string? Value, int Start, int End, SourceLocation Location);
