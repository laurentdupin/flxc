using Flx.Compiler.Frontend;

namespace Flx.Compiler.Preprocessing;

internal sealed class PreprocessedSource
{
    public required SourceFile SourceFile { get; init; }
    public required string Text { get; init; }
    public string? TemporaryPath { get; init; }
}
