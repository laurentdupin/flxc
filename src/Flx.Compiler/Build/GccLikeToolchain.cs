namespace Flx.Compiler.Build;

internal sealed class GccLikeToolchain : ICToolchain
{
    public Task<BuildResult> CompileObjectAsync(string sourcePath, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var args = CommonCompileArgs(options).ToList();
        args.Add(sourcePath);
        args.Add("-c");
        args.Add("-o");
        args.Add(outputPath);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    public Task<BuildResult> LinkExecutableAsync(IEnumerable<string> objectPaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var args = new List<string>();
        args.AddRange(objectPaths);
        args.AddRange(LinkArgs(options));
        args.Add("-o");
        args.Add(outputPath);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    public Task<BuildResult> CompileAndLinkAsync(IEnumerable<string> sourcePaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var args = CommonCompileArgs(options).ToList();
        args.AddRange(sourcePaths);
        args.AddRange(LinkArgs(options));
        args.Add("-o");
        args.Add(outputPath);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    private static IEnumerable<string> CommonCompileArgs(BuildOptions options)
    {
        foreach (var includeDir in options.IncludeDirs)
            yield return "-I" + includeDir;
        foreach (var define in options.Defines)
            yield return "-D" + define;
        foreach (var undefine in options.Undefines)
            yield return "-U" + undefine;
        foreach (var flag in options.CFlags)
            yield return flag;
    }

    private static IEnumerable<string> LinkArgs(BuildOptions options)
    {
        foreach (var libraryDir in options.LibraryDirs)
            yield return "-L" + libraryDir;
        foreach (var library in options.Libraries)
            yield return IsDirectLibraryPath(library) ? library : "-l" + library;
        foreach (var flag in options.LdFlags)
            yield return flag;
    }

    private static bool IsDirectLibraryPath(string library)
    {
        return Path.IsPathRooted(library) ||
               library.Contains(Path.DirectorySeparatorChar) ||
               library.Contains(Path.AltDirectorySeparatorChar) ||
               library.EndsWith(".a", StringComparison.OrdinalIgnoreCase) ||
               library.EndsWith(".lib", StringComparison.OrdinalIgnoreCase);
    }
}
