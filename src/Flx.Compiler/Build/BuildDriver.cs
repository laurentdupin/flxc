using Flx.Compiler.Cli;
using Flx.Compiler.Codegen.C;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Metadata;
using Flx.Compiler.Preprocessing;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Build;

internal sealed class BuildDriver
{
    public async Task<int> RunAsync(CommandLineOptions options, TextWriter output, TextWriter error)
    {
        var diagnostics = new DiagnosticBag();
        var sourceFiles = LoadSources(options, diagnostics);
        if (diagnostics.HasErrors)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var outputDirectory = DetermineGeneratedDirectory(options, out var shouldDeleteDirectory);
        Directory.CreateDirectory(outputDirectory);

        var detector = new ToolchainDetector();
        ICToolchain? toolchain = null;
        BuildOptions? buildOptions = null;

        if (!options.NoPreprocess)
        {
            (toolchain, buildOptions) = detector.Detect(options, diagnostics);
            if (diagnostics.HasErrors || buildOptions is null)
            {
                diagnostics.PrintTo(error);
                return 1;
            }

            sourceFiles = (await new PreprocessorDriver(new ExternalCPreprocessor()).PreprocessAsync(
                sourceFiles,
                options,
                buildOptions,
                outputDirectory,
                diagnostics,
                output,
                error)).ToList();

            if (diagnostics.HasErrors)
            {
                diagnostics.PrintTo(error);
                return 1;
            }
        }
        else if (options.EmitPreprocessed)
        {
            await EmitOriginalAsPreprocessedAsync(sourceFiles, outputDirectory);
        }

        if (options.EmitPreprocessed)
            return 0;

        var units = ParseSources(sourceFiles, diagnostics);
        var requireSchedule = !options.CompileOnly && !options.NoMain;
        var validateScheduleTargets = !options.CompileOnly;
        var model = new SemanticAnalyzer(diagnostics).Analyze(units, requireSchedule, validateScheduleTargets);

        if (diagnostics.HasErrors)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var generation = await GenerateAsync(model, options, outputDirectory, shouldDeleteDirectory);

        if (options.EmitC)
            return 0;

        if (toolchain is null || buildOptions is null)
            (toolchain, buildOptions) = detector.Detect(options, diagnostics);

        if (diagnostics.HasErrors || toolchain is null || buildOptions is null)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var result = options.CompileOnly
            ? await CompileObjectsAsync(options, generation, toolchain, buildOptions, output, error)
            : await BuildExecutableAsync(options, generation, toolchain, buildOptions, output, error);

        if (result != 0)
            return result;

        if (generation.ShouldDeleteDirectory)
            TryDeleteDirectory(generation.OutputDirectory);

        return 0;
    }

    private static List<SourceFile> LoadSources(CommandLineOptions options, DiagnosticBag diagnostics)
    {
        var sources = new List<SourceFile>();
        foreach (var input in options.InputFiles)
        {
            if (!File.Exists(input))
            {
                diagnostics.Report("FLX9005", $"input file '{input}' does not exist.");
                continue;
            }

            sources.Add(new SourceFile(input, File.ReadAllText(input)));
        }

        return sources;
    }

    private static List<CompilationUnitSyntax> ParseSources(IEnumerable<SourceFile> sourceFiles, DiagnosticBag diagnostics)
    {
        var units = new List<CompilationUnitSyntax>();
        foreach (var source in sourceFiles)
        {
            var lexer = new Lexer(source, diagnostics);
            var tokens = lexer.Lex();
            var parser = new Parser(source, tokens, diagnostics);
            units.Add(parser.ParseCompilationUnit());
        }

        return units;
    }

    private static async Task EmitOriginalAsPreprocessedAsync(IReadOnlyList<SourceFile> sourceFiles, string outputDirectory)
    {
        var preprocessedDirectory = Path.Combine(outputDirectory, "preprocessed");
        Directory.CreateDirectory(preprocessedDirectory);

        foreach (var source in sourceFiles)
        {
            var outputPath = Path.Combine(preprocessedDirectory, Path.GetFileName(source.DisplayPath) + ".pp.flx");
            await File.WriteAllTextAsync(outputPath, source.Text);
        }
    }

    private static async Task<GenerationResult> GenerateAsync(
        CompilationModel model,
        CommandLineOptions options,
        string outputDirectory,
        bool shouldDeleteDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var cGenerator = new CGenerator();
        var headerGenerator = new CHeaderGenerator();
        var generatedSources = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string umbrellaHeaderFileName = "flx_program.g.h";

        if (model.RequiresRuntime)
        {
            var runtime = new CRuntimeGenerator();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "flx_runtime.g.h"), runtime.GenerateHeader(model));
            var runtimeSource = Path.Combine(outputDirectory, "flx_runtime.g.c");
            await File.WriteAllTextAsync(runtimeSource, runtime.GenerateSource(model));
            generatedSources.Add(runtimeSource);
        }

        var moduleHeaders = new List<ModuleHeader>();
        foreach (var module in model.Modules)
        {
            var headerFileName = UniqueFileName(Path.GetFileName(module.SourceFile.DisplayPath) + ".g.h", usedNames);
            var headerPath = Path.Combine(outputDirectory, headerFileName);
            await File.WriteAllTextAsync(headerPath, headerGenerator.Generate(module, model, headerFileName));
            moduleHeaders.Add(new ModuleHeader(module, headerFileName));
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, umbrellaHeaderFileName),
            new CUmbrellaHeaderGenerator().Generate(model, moduleHeaders));

        foreach (var moduleHeader in moduleHeaders)
        {
            var module = moduleHeader.Module;
            var cFileName = UniqueFileName(Path.GetFileName(module.SourceFile.DisplayPath) + ".g.c", usedNames);
            var cPath = Path.Combine(outputDirectory, cFileName);
            await File.WriteAllTextAsync(cPath, cGenerator.Generate(module, model, options.AbsoluteLineDirectives, umbrellaHeaderFileName));
            generatedSources.Add(cPath);

            var metadataPath = Path.Combine(outputDirectory, Path.GetFileName(module.SourceFile.DisplayPath) + ".meta.json");
            await MetadataWriter.WriteAsync(module, cPath, metadataPath);
        }

        string? mainSource = null;
        if (!options.CompileOnly && !options.NoMain && model.Schedule is not null)
        {
            mainSource = Path.Combine(outputDirectory, "flx_main.g.c");
            await File.WriteAllTextAsync(mainSource, new CMainGenerator().Generate(model, umbrellaHeaderFileName));
            generatedSources.Add(mainSource);
        }

        if (!string.IsNullOrWhiteSpace(options.GeneratedListPath))
            await WriteGeneratedListAsync(options.GeneratedListPath, generatedSources);

        return new GenerationResult(outputDirectory, shouldDeleteDirectory, generatedSources, mainSource);
    }

    private static async Task WriteGeneratedListAsync(string generatedListPath, IReadOnlyList<string> generatedSources)
    {
        var fullPath = Path.GetFullPath(generatedListPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var lines = generatedSources
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase);

        await File.WriteAllLinesAsync(fullPath, lines);
    }

    private static string DetermineGeneratedDirectory(CommandLineOptions options, out bool shouldDeleteDirectory)
    {
        if (!string.IsNullOrWhiteSpace(options.ObjDir))
        {
            shouldDeleteDirectory = false;
            return Path.GetFullPath(options.ObjDir);
        }

        if (options.EmitC || options.EmitPreprocessed || options.CompileOnly || options.KeepC || options.KeepPreprocessed)
        {
            shouldDeleteDirectory = false;
            return Path.GetFullPath(Path.Combine("build", "flxc"));
        }

        shouldDeleteDirectory = true;
        return Path.Combine(Path.GetTempPath(), "flxc", Guid.NewGuid().ToString("N"));
    }

    private static async Task<int> CompileObjectsAsync(
        CommandLineOptions options,
        GenerationResult generation,
        ICToolchain toolchain,
        BuildOptions buildOptions,
        TextWriter output,
        TextWriter error)
    {
        for (var i = 0; i < generation.GeneratedSources.Count; i++)
        {
            var source = generation.GeneratedSources[i];
            var outputPath = DetermineObjectOutputPath(options, source);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            var result = await toolchain.CompileObjectAsync(source, outputPath, buildOptions, output, error);
            if (!result.Success)
                return result.ExitCode;
        }

        return 0;
    }

    private static async Task<int> BuildExecutableAsync(
        CommandLineOptions options,
        GenerationResult generation,
        ICToolchain toolchain,
        BuildOptions buildOptions,
        TextWriter output,
        TextWriter error)
    {
        var outputPath = options.OutputPath ?? DefaultExecutableName();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var result = await toolchain.CompileAndLinkAsync(generation.GeneratedSources, outputPath, buildOptions, output, error);
        return result.Success ? 0 : result.ExitCode;
    }

    private static string DetermineObjectOutputPath(CommandLineOptions options, string sourcePath)
    {
        if (options.OutputPath is not null &&
            options.InputFiles.Count == 1 &&
            !Path.GetFileName(sourcePath).Equals("flx_runtime.g.c", StringComparison.OrdinalIgnoreCase))
        {
            return options.OutputPath;
        }

        var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";
        var fileName = Path.GetFileNameWithoutExtension(sourcePath) + extension;
        return Path.Combine(options.ObjDir is null ? Path.Combine("build", "flxc") : options.ObjDir, fileName);
    }

    private static string DefaultExecutableName() => OperatingSystem.IsWindows() ? "a.exe" : "a.out";

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Intermediate cleanup failure should not fail a successful build.
        }
    }

    private sealed record GenerationResult(
        string OutputDirectory,
        bool ShouldDeleteDirectory,
        IReadOnlyList<string> GeneratedSources,
        string? MainSource);
}
