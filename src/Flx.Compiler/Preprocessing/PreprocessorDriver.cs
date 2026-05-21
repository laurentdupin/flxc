using Flx.Compiler.Build;
using Flx.Compiler.Cli;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;

namespace Flx.Compiler.Preprocessing;

internal sealed class PreprocessorDriver
{
    private readonly IPreprocessor _preprocessor;

    public PreprocessorDriver(IPreprocessor preprocessor)
    {
        _preprocessor = preprocessor;
    }

    public async Task<IReadOnlyList<SourceFile>> PreprocessAsync(
        IReadOnlyList<SourceFile> sources,
        CommandLineOptions commandLineOptions,
        BuildOptions buildOptions,
        string outputDirectory,
        DiagnosticBag diagnostics,
        TextWriter stdout,
        TextWriter stderr)
    {
        var outputDirectoryFullPath = Path.GetFullPath(outputDirectory);
        var preprocessedDirectory = Path.Combine(outputDirectoryFullPath, "preprocessed");
        Directory.CreateDirectory(preprocessedDirectory);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceFile>();

        foreach (var source in sources)
        {
            var outputPath = Path.Combine(preprocessedDirectory, UniqueFileName(Path.GetFileName(source.DisplayPath) + ".pp.flx", usedNames));
            var options = new PreprocessorOptions
            {
                InputPath = source.FullPath,
                InputText = source.Text,
                WorkingDirectory = preprocessedDirectory,
                CompilerPath = buildOptions.CompilerPath,
                CompilerMode = buildOptions.CompilerMode,
                IncludeDirs = commandLineOptions.IncludeDirs,
                Defines = commandLineOptions.Defines,
                Undefines = commandLineOptions.Undefines,
                RawPreprocessorFlags = commandLineOptions.PreprocessorFlags,
                Verbose = commandLineOptions.Verbose,
                KeepPreprocessed = commandLineOptions.KeepPreprocessed || commandLineOptions.EmitPreprocessed,
                OutputPath = outputPath
            };

            try
            {
                var preprocessed = await _preprocessor.PreprocessAsync(source, options, stdout, stderr, CancellationToken.None);
                result.Add(preprocessed.SourceFile);
            }
            catch (PreprocessorException ex)
            {
                diagnostics.Report("FLX0400", ex.Message, source.GetLocation(0));
            }
        }

        return result;
    }

    private static string UniqueFileName(string fileName, HashSet<string> usedNames)
    {
        if (usedNames.Add(fileName))
            return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidate = $"{stem}.{index}{extension}";
            if (usedNames.Add(candidate))
                return candidate;
            index++;
        }
    }
}
