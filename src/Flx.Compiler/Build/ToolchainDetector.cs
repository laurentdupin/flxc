using System.Runtime.InteropServices;
using Flx.Compiler.Cli;
using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Build;

internal sealed class ToolchainDetector
{
    public (ICToolchain? Toolchain, BuildOptions? Options) Detect(CommandLineOptions commandLineOptions, DiagnosticBag diagnostics)
    {
        var compiler = SelectCompiler(commandLineOptions);

        if (string.Equals(compiler, "auto", StringComparison.OrdinalIgnoreCase))
            compiler = null;

        var compilerPath = compiler is not null ? FindCommand(compiler) : FindDefaultCompiler();
        if (compilerPath is null)
        {
            diagnostics.Report("FLX0300", "no C compiler found. Use --cc, FLXC_CC, or CC to choose one.");
            return (null, null);
        }

        var mode = ResolveMode(commandLineOptions.CCompilerMode, compilerPath);
        if (mode is null)
        {
            diagnostics.Report("FLX0301", $"unknown C compiler mode '{commandLineOptions.CCompilerMode}'.");
            return (null, null);
        }

        ICToolchain toolchain = mode == "msvc" ? new MsvcToolchain() : new GccLikeToolchain();
        var buildOptions = new BuildOptions
        {
            CompilerPath = compilerPath,
            CompilerMode = mode,
            IncludeDirs = commandLineOptions.IncludeDirs,
            LibraryDirs = commandLineOptions.LibraryDirs,
            Libraries = commandLineOptions.Libraries,
            Defines = commandLineOptions.Defines,
            Undefines = commandLineOptions.Undefines,
            CFlags = commandLineOptions.CFlags,
            LdFlags = commandLineOptions.LdFlags,
            Verbose = commandLineOptions.Verbose
        };

        return (toolchain, buildOptions);
    }

    private static string? SelectCompiler(CommandLineOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CCompiler))
            return options.CCompiler;

        var flxcCc = Environment.GetEnvironmentVariable("FLXC_CC");
        if (!string.IsNullOrWhiteSpace(flxcCc))
            return flxcCc;

        var cc = Environment.GetEnvironmentVariable("CC");
        if (!string.IsNullOrWhiteSpace(cc))
            return cc;

        return null;
    }

    private static string? FindDefaultCompiler()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "cl.exe", "clang-cl.exe", "clang.exe", "gcc.exe", "cc.exe" }
            : new[] { "cc", "clang", "gcc" };

        foreach (var candidate in candidates)
        {
            var path = FindCommand(candidate);
            if (path is not null)
                return path;
        }

        return null;
    }

    private static string? ResolveMode(string? requestedMode, string compilerPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedMode))
        {
            var normalized = requestedMode.ToLowerInvariant();
            return normalized is "gcc" or "clang" ? "gcc" :
                normalized == "msvc" ? "msvc" :
                null;
        }

        var fileName = Path.GetFileNameWithoutExtension(compilerPath);
        return fileName.Equals("cl", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("clang-cl", StringComparison.OrdinalIgnoreCase)
            ? "msvc"
            : "gcc";
    }

    private static string? FindCommand(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? command : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, command);
                if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    candidate += extension;

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
