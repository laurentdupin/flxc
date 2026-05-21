using System.Diagnostics;
using Flx.Compiler.Frontend;

namespace Flx.Compiler.Preprocessing;

internal sealed class ExternalCPreprocessor : IPreprocessor
{
    public async Task<PreprocessedSource> PreprocessAsync(
        SourceFile source,
        PreprocessorOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.WorkingDirectory);

        var outputPath = options.OutputPath ?? Path.Combine(
            options.WorkingDirectory,
            Path.GetFileName(source.DisplayPath) + ".pp.flx");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var result = string.Equals(options.CompilerMode, "msvc", StringComparison.OrdinalIgnoreCase)
            ? await RunMsvcAsync(options, outputPath, stdout, stderr, cancellationToken)
            : await RunGccLikeAsync(options, outputPath, stdout, stderr, cancellationToken);

        if (!result.Success)
            throw new PreprocessorException($"FLX preprocessing failed for '{source.DisplayPath}'.");

        var text = await File.ReadAllTextAsync(outputPath, cancellationToken);
        return new PreprocessedSource
        {
            SourceFile = new SourceFile(source.FullPath, text, source.OriginalText),
            Text = text,
            TemporaryPath = outputPath
        };
    }

    private static async Task<PreprocessProcessResult> RunGccLikeAsync(
        PreprocessorOptions options,
        string outputPath,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-E", "-x", "c", "-P" };
        AddCommonGccLikeArgs(args, options);
        args.AddRange(options.RawPreprocessorFlags);
        args.Add(options.InputPath);
        args.Add("-o");
        args.Add(outputPath);
        return await RunProcessAsync(options.CompilerPath, args, options.Verbose, stdout, stderr, cancellationToken);
    }

    private static async Task<PreprocessProcessResult> RunMsvcAsync(
        PreprocessorOptions options,
        string outputPath,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "/nologo", "/EP", "/TC" };
        AddCommonMsvcArgs(args, options);
        args.AddRange(options.RawPreprocessorFlags);
        args.Add(options.InputPath);

        var result = await RunProcessAsync(options.CompilerPath, args, options.Verbose, stdout, stderr, cancellationToken);
        if (result.Success)
            await File.WriteAllTextAsync(outputPath, result.StandardOutput, cancellationToken);

        return result;
    }

    private static void AddCommonGccLikeArgs(List<string> args, PreprocessorOptions options)
    {
        foreach (var includeDir in options.IncludeDirs)
            args.Add("-I" + includeDir);
        foreach (var define in options.Defines)
            args.Add("-D" + define);
        foreach (var undefine in options.Undefines)
            args.Add("-U" + undefine);
    }

    private static void AddCommonMsvcArgs(List<string> args, PreprocessorOptions options)
    {
        foreach (var includeDir in options.IncludeDirs)
            args.Add("/I" + includeDir);
        foreach (var define in options.Defines)
            args.Add("/D" + define);
        foreach (var undefine in options.Undefines)
            args.Add("/U" + undefine);
    }

    private static async Task<PreprocessProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> baseArguments,
        bool verbose,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var arguments = baseArguments.ToArray();
        if (verbose)
            await stdout.WriteLineAsync(FormatCommand(fileName, arguments));

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new PreprocessProcessResult(1, "", $"failed to start C preprocessor '{fileName}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(standardError))
            await stderr.WriteAsync(standardError);

        return new PreprocessProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string FormatCommand(string fileName, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        if (value.Any(char.IsWhiteSpace) || value.Contains('"'))
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return value;
    }

    private sealed record PreprocessProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Success => ExitCode == 0;
    }
}
