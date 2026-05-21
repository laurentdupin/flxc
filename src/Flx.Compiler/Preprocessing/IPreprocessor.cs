using Flx.Compiler.Frontend;

namespace Flx.Compiler.Preprocessing;

internal interface IPreprocessor
{
    Task<PreprocessedSource> PreprocessAsync(
        SourceFile source,
        PreprocessorOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken);
}
