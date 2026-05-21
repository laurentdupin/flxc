namespace Flx.Compiler.Build;

internal sealed class MsvcToolchain : ICToolchain
{
    public Task<BuildResult> CompileObjectAsync(string sourcePath, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var args = CommonCompileArgs(options).ToList();
        args.Add("/c");
        args.Add(sourcePath);
        args.Add("/Fo:" + outputPath);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    public Task<BuildResult> LinkExecutableAsync(IEnumerable<string> objectPaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var args = new List<string> { "/nologo" };
        args.AddRange(objectPaths);
        args.Add("/Fe:" + outputPath);
        AddLinkArgs(args, options);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    public Task<BuildResult> CompileAndLinkAsync(IEnumerable<string> sourcePaths, string outputPath, BuildOptions options, TextWriter output, TextWriter error)
    {
        var sources = sourcePaths.ToArray();
        var args = CommonCompileArgs(options).ToList();
        if (sources.Length > 0 && Path.GetDirectoryName(sources[0]) is { } objectDir)
            args.Add("/Fo:" + EnsureTrailingSeparator(objectDir));
        args.AddRange(sources);
        args.Add("/Fe:" + outputPath);
        AddLinkArgs(args, options);
        return ProcessRunner.RunAsync(options.CompilerPath, args, options.Verbose, output, error);
    }

    private static IEnumerable<string> CommonCompileArgs(BuildOptions options)
    {
        yield return "/nologo";
        foreach (var includeDir in options.IncludeDirs)
            yield return "/I" + includeDir;
        foreach (var define in options.Defines)
            yield return "/D" + define;
        foreach (var undefine in options.Undefines)
            yield return "/U" + undefine;
        foreach (var flag in options.CFlags)
            yield return flag;
    }

    private static void AddLinkArgs(List<string> args, BuildOptions options)
    {
        if (options.LibraryDirs.Count == 0 && options.Libraries.Count == 0 && options.LdFlags.Count == 0)
            return;

        args.Add("/link");

        foreach (var libraryDir in options.LibraryDirs)
            args.Add("/LIBPATH:" + libraryDir);
        foreach (var library in options.Libraries)
            args.Add(library.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ? library : library + ".lib");
        args.AddRange(options.LdFlags);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
