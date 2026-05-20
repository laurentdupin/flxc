namespace Flx.Compiler.Diagnostics;

internal readonly record struct SourceLocation(string FilePath, int Line, int Column, int Position)
{
    public override string ToString() => $"{FilePath}({Line},{Column})";
}
