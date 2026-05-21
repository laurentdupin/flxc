namespace Flx.Compiler.Build;

internal interface ICToolchain
{
    Task<BuildResult> CompileObjectAsync(string sourcePath, string outputPath, BuildOptions options, TextWriter output, TextWriter error);
    Task<BuildResult> LinkExecutableAsync(IEnumerable<string> objectPaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error);
    Task<BuildResult> CompileAndLinkAsync(IEnumerable<string> sourcePaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error);
}
