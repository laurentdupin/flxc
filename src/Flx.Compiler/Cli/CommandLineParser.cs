using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Cli;

internal static class CommandLineParser
{
    public static CommandLineOptions Parse(string[] args, DiagnosticBag diagnostics)
    {
        var options = new CommandLineOptions();
        var consumeInputsOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (consumeInputsOnly)
            {
                options.InputFiles.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                consumeInputsOnly = true;
                continue;
            }

            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            if (arg == "--version")
            {
                options.ShowVersion = true;
                continue;
            }

            if (arg == "-c")
            {
                options.CompileOnly = true;
                continue;
            }

            if (arg == "--emit-c")
            {
                options.EmitC = true;
                continue;
            }

            if (arg == "--emit-pp")
            {
                options.EmitPreprocessed = true;
                continue;
            }

            if (arg == "--keep-c")
            {
                options.KeepC = true;
                continue;
            }

            if (arg == "--keep-pp")
            {
                options.KeepPreprocessed = true;
                continue;
            }

            if (arg == "--no-preprocess")
            {
                options.NoPreprocess = true;
                continue;
            }

            if (arg == "--verbose")
            {
                options.Verbose = true;
                continue;
            }

            if (arg is "--no-main" or "--library")
            {
                options.NoMain = true;
                continue;
            }

            if (arg == "--absolute-line-directives")
            {
                options.AbsoluteLineDirectives = true;
                continue;
            }

            if (arg == "-o")
            {
                options.OutputPath = RequireValue(args, ref i, arg, diagnostics);
                continue;
            }

            if (arg.StartsWith("-o", StringComparison.Ordinal) && arg.Length > 2)
            {
                options.OutputPath = arg[2..];
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--obj-dir", diagnostics, out var objDir))
            {
                options.ObjDir = objDir;
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--generated-list", diagnostics, out var generatedList))
            {
                options.GeneratedListPath = generatedList;
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--diagnostics-format", diagnostics, out var diagnosticsFormat))
            {
                options.DiagnosticsFormat = diagnosticsFormat;
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--cc", diagnostics, out var cc))
            {
                options.CCompiler = cc;
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--cc-mode", diagnostics, out var ccMode))
            {
                options.CCompilerMode = ccMode;
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--cflag", diagnostics, out var cflag))
            {
                options.CFlags.Add(cflag);
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--ldflag", diagnostics, out var ldflag))
            {
                options.LdFlags.Add(ldflag);
                continue;
            }

            if (TryReadLongOptionValue(args, ref i, arg, "--ppflag", diagnostics, out var ppflag))
            {
                options.PreprocessorFlags.Add(ppflag);
                continue;
            }

            if (TryReadShortOptionValue(args, ref i, arg, "-I", diagnostics, out var includeDir))
            {
                options.IncludeDirs.Add(includeDir);
                continue;
            }

            if (TryReadShortOptionValue(args, ref i, arg, "-L", diagnostics, out var libraryDir))
            {
                options.LibraryDirs.Add(libraryDir);
                continue;
            }

            if (TryReadShortOptionValue(args, ref i, arg, "-l", diagnostics, out var library))
            {
                options.Libraries.Add(library);
                continue;
            }

            if (TryReadShortOptionValue(args, ref i, arg, "-D", diagnostics, out var define))
            {
                options.Defines.Add(define);
                continue;
            }

            if (TryReadShortOptionValue(args, ref i, arg, "-U", diagnostics, out var undefine))
            {
                options.Undefines.Add(undefine);
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Report("FLX9001", $"unknown command-line option '{arg}'.");
                continue;
            }

            options.InputFiles.Add(arg);
        }

        if (!options.ShowHelp && !options.ShowVersion && options.InputFiles.Count == 0)
            diagnostics.Report("FLX9002", "no input files specified.");

        if (options.CompileOnly && options.InputFiles.Count > 1 && options.OutputPath is not null)
            diagnostics.Report("FLX9003", "-c with multiple input files cannot use a single -o output path.");

        if (options.DiagnosticsFormat is not null &&
            !options.DiagnosticsFormat.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !options.DiagnosticsFormat.Equals("msbuild", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Report("FLX9006", $"unknown diagnostics format '{options.DiagnosticsFormat}'. Expected 'default' or 'msbuild'.");
        }

        return options;
    }

    private static bool TryReadLongOptionValue(
        string[] args,
        ref int index,
        string arg,
        string option,
        DiagnosticBag diagnostics,
        out string value)
    {
        if (arg == option)
        {
            value = RequireValue(args, ref index, option, diagnostics);
            return true;
        }

        var prefix = option + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryReadShortOptionValue(
        string[] args,
        ref int index,
        string arg,
        string option,
        DiagnosticBag diagnostics,
        out string value)
    {
        if (arg == option)
        {
            value = RequireValue(args, ref index, option, diagnostics);
            return true;
        }

        if (arg.StartsWith(option, StringComparison.Ordinal) && arg.Length > option.Length)
        {
            value = arg[option.Length..];
            return true;
        }

        value = "";
        return false;
    }

    private static string RequireValue(string[] args, ref int index, string option, DiagnosticBag diagnostics)
    {
        if (index + 1 < args.Length)
            return args[++index];

        diagnostics.Report("FLX9004", $"missing value for option '{option}'.");
        return "";
    }
}
